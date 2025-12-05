using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using TShockAPI;

namespace AutoCompile;

#region 编译结果类
public class CompResult
{
    public bool Ok;
    public string Msg;
    public object Data;

    private CompResult(bool ok, string msg, object data = null)
    {
        Ok = ok;
        Msg = msg;
        Data = data;
    }

    public static CompResult Success(string msg = "完成", object data = null)
        => new CompResult(true, msg, data);

    public static CompResult Fail(string msg)
        => new CompResult(false, msg);
}
#endregion

internal class Compiler
{
    public static readonly object LockObj = new();

    // 静态构造函数，只会执行一次
    static Compiler()
    {
        try
        {
            // 修复中文编码用的
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            TShock.Log.ConsoleInfo("【自动编译】 编码提供程序已注册");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 注册编码提供程序失败: {ex.Message}");
        }
    }

    #region 编译主方法
    public static CompResult CompAll(string path = "")
    {
        try
        {
            var fullPath = string.IsNullOrEmpty(path)
            ? Path.Combine(Configuration.Paths, "源码")
            : Path.GetFullPath(Path.Combine(Configuration.Paths, path));


            var safeResult = Utils.CheckFileSize(fullPath);
            if (!safeResult.Ok)
                return safeResult;


            // 创建目录
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                return CompResult.Fail("创建目录完成，请放入.cs文件");
            }

            // 查找文件
            var searchOpt = AutoCompile.Config.IncludeSub
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var csFiles = Directory.GetFiles(fullPath, "*.cs", searchOpt);
            if (csFiles.Length == 0)
                return CompResult.Fail("未找到.cs文件");

            // 编译
            var sw = Stopwatch.StartNew();
            CompResult result;

            lock (LockObj)
            {
                result = BuildAll(csFiles);
            }

            // 停止计时
            sw.Stop();

            if (result.Ok)
            {
                var msg = $"编译完成: {csFiles.Length}文件 {sw.ElapsedMilliseconds}ms";
                if (result.Data is List<string> files)
                {
                    msg += $" 生成{files.Count}个DLL";
                }
                return CompResult.Success(msg, result.Data);
            }

            return result;
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"编译异常: {ex.Message}");
        }
    }
    #endregion

    #region 构建逻辑（简化版）
    private static CompResult BuildAll(string[] files)
    {
        var trees = new List<SyntaxTree>();
        var refs = new HashSet<string>();
        var skp = new List<string>();
        var err = new List<string>();

        try
        {
            // 清理旧文件
            Utils.CleanOutFiles();

            // 添加引用
            AddRefs(refs);

            // 遍历所有文件
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length == 0)
                    {
                        skp.Add($"{Path.GetFileName(f)} (空)");
                        continue;
                    }

                    var code = Utils.ReadAndFixFile(f);
                    // code = RemoveUsings(code); // 强制移除指定using

                    if (string.IsNullOrWhiteSpace(code))
                    {
                        skp.Add($"{Path.GetFileName(f)} (空白)");
                        continue;
                    }

                    if (!Utils.IsValidCSharpCode(code))
                    {
                        skp.Add($"{Path.GetFileName(f)} (无效)");
                        continue;
                    }

                    var uc = AddUsings(code);

                    // 解析语法树
                    var tree = CSharpSyntaxTree.ParseText(
                        text: uc,
                        options: CSharpParseOptions.Default.WithLanguageVersion(Utils.GetLangVer()),
                        path: f,
                        encoding: Encoding.UTF8
                    );
                    trees.Add(tree);
                }
                catch (Exception ex)
                {
                    err.Add($"{Path.GetFileName(f)}: {ex.Message}");
                    TShock.Log.ConsoleError($"【自动编译】 解析 {Path.GetFileName(f)} 失败: {ex.Message}");
                }
            }

            // 记录跳过的文件
            if (skp.Count > 0 || err.Count > 0)
            {
                LogsMag.LogSkip(skp, err);
            }

            // 无有效文件
            if (trees.Count == 0)
            {
                var msg = "无有效.cs文件";
                if (skp.Count + err.Count > 0)
                {
                    msg += $"，跳过了{skp.Count + err.Count}个文件";
                }
                return CompResult.Fail(msg);
            }

            // 创建元数据引用
            var rfs = refs
                .Where(File.Exists)
                .Select(r => MetadataReference.CreateFromFile(r))
                .ToList();

            if (rfs.Count == 0)
                return CompResult.Fail("无有效引用");

            // 获取插件名称
            var pluginName = GetPluginName(trees);
            if (string.IsNullOrEmpty(pluginName))
                pluginName = "MyPlugin";

            var outDir = Path.Combine(Configuration.Paths, "编译输出");
            var dllName = $"{Utils.CleanName(pluginName)}.dll";
            var dllPath = Path.Combine(outDir, dllName);
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

            EmitResult er = CompileWithRetry(trees, rfs, pluginName, dllPath, pdbPath, AutoCompile.Config.RetryCount);

            // 编译失败处理
            if (!er.Success)
            {
                return LogsMag.ErrorMess(pluginName, er);
            }

            LogsMag.LogCompile(pluginName, dllPath, pdbPath);
            LogsMag.ClearLogs();
            return CompResult.Success("编译完成", new List<string> { dllPath });
        }
        catch (OutOfMemoryException)
        {
            return CompResult.Fail("内存不足");
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"构建失败: {ex.Message}");
        }
        finally
        {
            // 清理资源
            try
            {
                // 在现有GC清理前添加内存信息 - 保留
                var memInfo = LogsMag.GetMemInfo();
                TShock.Log.ConsoleInfo($"【自动编译】 编译完成，{memInfo}");

                // 清除所有引用
                trees?.Clear();
                trees = null;
                skp?.Clear();
                skp = null;
                err?.Clear();
                err = null;
                refs?.Clear();
                refs = null;

                // 删除原来的复杂GC清理，改为简单版本
                var mem1 = GC.GetTotalMemory(false);
                GC.Collect();  // 简单GC，不强制参数
                var mem2 = GC.GetTotalMemory(true);
                var freed = mem1 - mem2;
                if (freed > 1024 * 1024)
                {
                    TShock.Log.ConsoleInfo($"【自动编译】 释放内存 {freed / 1024 / 1024:F2} MB");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleWarn($"【自动编译】 内存清理异常: {ex.Message}");
            }
        }
    }
    #endregion

    #region 为代码添加默认 using
    private static string AddUsings(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        // 从配置中获取默认 using 指令
        // 从配置获取并格式化
        var defList = AutoCompile.Config.Usings;
        var fmtUsgs = FmtUsings(defList);

        if (string.IsNullOrEmpty(fmtUsgs))
            return code;

        // 检查代码中是否已经有这些 using（避免重复）
        var existing = GetExistUsings(code);

        // 过滤掉已经存在的 using
        var ToAdd = FilterUsings(fmtUsgs, existing);

        if (string.IsNullOrEmpty(ToAdd))
            return code;

        // 总是添加到文件最开头
        return ToAdd + code;
    }
    #endregion

    #region 格式化 using
    private static string FmtUsings(List<string> usgs)
    {
        if (usgs == null || usgs.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var usg in usgs)
        {
            if (string.IsNullOrWhiteSpace(usg))
                continue;

            var trim = usg.Trim();

            // 已完整
            if (trim.StartsWith("using ") && trim.EndsWith(";"))
            {
                sb.AppendLine(trim);
            }
            // 需补充
            else
            {
                sb.AppendLine($"using {trim};");
            }
        }

        return sb.ToString();
    }
    #endregion

    #region 提取代码中已有的 using 指令
    private static List<string> GetExistUsings(string code)
    {
        var usings = new List<string>();

        try
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            var Directives = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.ToString())
                .ToList();

            usings.AddRange(Directives);
        }
        catch
        {
            // 解析失败时使用简单方法
            var lines = code.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
                {
                    usings.Add(trimmed);
                }
            }
        }

        return usings;
    }
    #endregion

    #region 过滤掉重复的 using
    private static string FilterUsings(string fmtUsgs, List<string> exist)
    {
        var lines = fmtUsgs.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trim = line.Trim();

            // 取命名空间部分
            string nsOnly = GetNs(trim);

            // 查重复
            bool existFlag = exist.Any(ex =>
                SameNs(ex.Trim(), trim) || SameNs(ex.Trim(), nsOnly));

            if (!existFlag)
                result.AppendLine(trim);
        }

        return result.ToString();
    }

    // 取命名空间
    private static string GetNs(string usgStmt)
    {
        if (string.IsNullOrWhiteSpace(usgStmt))
            return string.Empty;

        var trim = usgStmt.Trim();

        if (trim.StartsWith("using "))
            trim = trim.Substring(6);

        if (trim.EndsWith(";"))
            trim = trim.Substring(0, trim.Length - 1);

        return trim.Trim();
    }

    // 比命名空间
    private static bool SameNs(string ex, string now)
    {
        var exNs = GetNs(ex);
        var nowNs = GetNs(now);

        return string.Equals(exNs, nowNs, StringComparison.OrdinalIgnoreCase);
    }
    #endregion

    #region 添加所有引用
    internal static void AddRefs(HashSet<string> refs)
    {
        try
        {
            TShock.Log.ConsoleInfo("【自动编译】 开始添加引用...");

            // 1. 添加TS程序集引用
            AddTShockReferences(refs);

            // 2. 系统程序集 - 添加更多基础程序集
            AddSystemReferences(refs);

            TShock.Log.ConsoleInfo($"【自动编译】 总共添加了 {refs.Count} 个引用\n (含bin文件夹 5个 + TShockAPI 1个)");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 添加引用失败: {ex.Message}");
        }
    }
    #endregion

    #region 系统程序集 - 添加更多基础程序集
    private static void AddSystemReferences(HashSet<string> refs)
    {
        try
        {
            int added = 0;

            // 获取.NET运行时的系统程序集目录
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);

            if (!string.IsNullOrEmpty(runtime))
            {
                var Asse = AutoCompile.Config.SystemAsse;

                foreach (var ass in Asse)
                {
                    var file = Path.Combine(runtime, ass);

                    if (File.Exists(file) && !refs.Contains(file))
                    {
                        refs.Add(file);
                        added++;
                    }
                    else
                    {
                        TShock.Log.ConsoleError($"【自动编译】 文件不存在 {file} ");
                    }
                }

                if (added > 0)
                    TShock.Log.ConsoleInfo($"【自动编译】 添加了 {added} 个系统程序集");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 添加系统引用失败: {ex.Message}");
        }
    }
    #endregion

    #region 添加TS程序集引用
    private static void AddTShockReferences(HashSet<string> refs)
    {
        try
        {
            var count = 0;
            var dir = Path.Combine(Configuration.Paths, "程序集");
            // 1. 首先添加插件指定“程序集”文件夹中的所有DLL文件
            if (Directory.Exists(dir))
            {
                var dllFiles = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories);
                foreach (var dllPath in dllFiles)
                {
                    // 确保文件存在且不是重复的
                    if (File.Exists(dllPath) && !refs.Contains(dllPath))
                    {
                        // 跳过可能损坏或无法加载的DLL
                        if (Utils.IsValidDll(dllPath))
                        {
                            refs.Add(dllPath);
                            count++;
                        }
                        else
                        {
                            TShock.Log.ConsoleWarn($"【自动编译】 跳过无效的程序集: {Path.GetFileName(dllPath)}");
                        }
                    }
                }
            }

            if (count > 0)
                TShock.Log.ConsoleInfo($"【自动编译】 从‘程序集’添加了 {count} 个引用");

            // 2.添加TShockAPI.dll
            var PluginsDir = Path.Combine(typeof(TShock).Assembly.Location, "ServerPlugins");
            var path2 = Path.Combine(PluginsDir, "TShockAPI.dll");
            if (File.Exists(path2) && !refs.Contains(path2))
            {
                refs.Add(path2);
            }

            // 3.添加TS运行核心文件（从bin目录）
            var OT = new[]
            {
                "OTAPI.dll",
                "OTAPI.Runtime.dll",
                "HttpServer.dll",
                "ModFramework.dll",
                "TerrariaServer.dll"
            };

            foreach (var f in OT)
            {
                var binDir = Path.Combine(typeof(TShock).Assembly.Location, "bin");
                var path3 = Path.Combine(binDir, f);
                if (File.Exists(path3) && !refs.Contains(path3))
                {
                    refs.Add(path3);
                }
            }

        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 扫描目录失败: {ex.Message}");
        }
    }
    #endregion

    #region 提取插件名称
    private static string GetPluginName(List<SyntaxTree> trees)
    {
        try
        {
            // 遍历组中的所有文件，查找插件信息
            foreach (var tree in trees)
            {
                var root = tree.GetRoot();

                // 查找继承自TerrariaPlugin的主类
                var MainClass = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(cls => cls.BaseList?.Types
                        .Any(t => t.Type.ToString().Contains("TerrariaPlugin")) == true);

                if (MainClass != null)
                {
                    // 查找Name属性
                    var nameProp = MainClass.DescendantNodes()
                        .OfType<PropertyDeclarationSyntax>()
                        .FirstOrDefault(p => p.Identifier.Text == "Name");

                    // 提取插件名称
                    if (nameProp is null) continue;

                    string name = NameFromProperty(nameProp, MainClass);

                    if (!string.IsNullOrEmpty(name))
                    {
                        TShock.Log.ConsoleInfo($"【自动编译】 在{Path.GetFileName(tree.FilePath)}中获取到插件名: {name}");
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 提取插件信息失败: {ex.Message}");
        }

        return string.Empty;
    }

    // 从属性中提取名称
    private static string NameFromProperty(PropertyDeclarationSyntax prop, ClassDeclarationSyntax cls)
    {
        if (prop == null) return cls.Identifier.Text;

        // 箭头函数形式
        if (prop.ExpressionBody?.Expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        // Getter形式
        var getter = prop.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.Text == "get");
        if (getter?.Body?.Statements.FirstOrDefault() is ReturnStatementSyntax Stmt &&
            Stmt.Expression is LiteralExpressionSyntax Expr)
        {
            return Expr.Token.ValueText;
        }

        return cls.Identifier.Text;
    }
    #endregion

    #region 编译与重试方法
    private static EmitResult CompileWithRetry(List<SyntaxTree>? trees, List<PortableExecutableReference> rfs, string pluginName, string dllPath, string pdbPath, int count)
    {
        TShock.Log.ConsoleInfo($"\n【自动编译】 开始编译 次数剩余{count} 次...");
        var er = CompileOnce(trees, rfs, pluginName, dllPath, pdbPath);

        // 如果编译成功或没有重试次数，直接返回
        if (er.Success || count <= 0) return er;

        // 检查是否需要重试
        if (ShouldRetry(er))
        {
            TShock.Log.ConsoleInfo("【自动编译】 检测到缺失命名空间错误，尝试重试编译...");

            // 分析错误，提取缺失的命名空间
            var miss = GetMiss(er);

            if (miss.Count > 0)
            {
                // 尝试移除有问题的using语句
                var newTrees = RemoveUsings(trees, miss);

                if (newTrees != null && newTrees.Count > 0)
                {
                    TShock.Log.ConsoleInfo($"【自动编译】 移除{miss.Count}个命名空间，开始重试...");
                    // 重试编译（递归调用，减少重试次数）
                    return CompileWithRetry(newTrees, rfs, pluginName, dllPath, pdbPath, count - 1);
                }
                else
                {
                    TShock.Log.ConsoleWarn("【自动编译】 无法移除有问题的using语句，重试失败");
                }
            }
            else
            {
                TShock.Log.ConsoleWarn("【自动编译】 未找到缺失的命名空间，无法重试");
            }
        }
        else
        {
            TShock.Log.ConsoleInfo("【自动编译】 错误类型不适合重试");
        }

        return er;
    }
    #endregion

    #region 编译一次
    private static EmitResult CompileOnce(List<SyntaxTree>? trees, List<PortableExecutableReference> rfs, string pluginName, string dllPath, string pdbPath)
    {
        CSharpCompilation comp = CreateComp(trees, rfs, pluginName);

        // 编译
        EmitResult er;
        using (var dStream = File.Create(dllPath))
        using (var pStream = File.Create(pdbPath))
        {
            er = comp.Emit(dStream, pStream);
        }

        return er;
    }
    #endregion

    #region 创建编译方法
    private static CSharpCompilation CreateComp(List<SyntaxTree>? trees, List<PortableExecutableReference> rfs, string pluginName)
    {
        // 创建编译（所有文件一起编译）
        var comp = CSharpCompilation.Create(
            Utils.CleanName(pluginName),
            trees,  // 直接使用所有文件
            rfs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                warningLevel: 0,
                assemblyIdentityComparer: AssemblyIdentityComparer.Default,
                allowUnsafe: true,
                platform: Platform.AnyCpu,
                checkOverflow: false,
                concurrentBuild: true
            ));

        // 添加目标框架特性
        string fw = @"[assembly: System.Runtime.Versioning.TargetFramework("".NET6.0"", FrameworkDisplayName = "".NET 6.0"")]";
        var fwTree = CSharpSyntaxTree.ParseText(fw,
            options: CSharpParseOptions.Default.WithLanguageVersion(Utils.GetLangVer()),
            encoding: Encoding.UTF8);
        comp = comp.AddSyntaxTrees(fwTree);
        return comp;
    }
    #endregion

    #region 应用重试编译方法
    private static bool ShouldRetry(EmitResult er)
    {
        if (er.Success) return false;

        int errorCount = er.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        TShock.Log.ConsoleInfo($"【自动编译】 总共 {errorCount} 个错误，开始重试");

        // 检查是否有缺失命名空间或程序集的错误
        foreach (var diag in er.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var msg = diag.GetMessage();

            // 检查是否包含缺失命名空间的错误模式（英文）
            if (msg.Contains("The type or namespace name") ||
                msg.Contains("are you missing a using directive") ||
                msg.Contains("are you missing an assembly reference") ||
                msg.Contains("could not be found"))
            {
                return true;
            }
        }

        TShock.Log.ConsoleInfo("【自动编译】 没有检测到需要重试的错误类型");
        return false;
    }
    #endregion

    #region 获取缺失命名空间
    private static List<string> GetMiss(EmitResult er)
    {
        var ms = new List<string>();

        foreach (var diag in er.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var msg = diag.GetMessage();

            // 尝试匹配英文格式1: The type or namespace name 'X' could not be found
            var match = Regex.Match(msg, @"The type or namespace name '([^']+)' could not be found");
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                AddMissing(ms, name);
                continue;
            }

            // 尝试匹配英文格式2: The type or namespace name 'X' does not exist in the namespace 'Y'
            match = Regex.Match(msg, @"The type or namespace name '([^']+)' does not exist in the namespace '([^']+)'");
            if (match.Success)
            {
                var typeName = match.Groups[1].Value;
                var namespaceName = match.Groups[2].Value;
                var fullName = $"{namespaceName}.{typeName}";
                AddMissing(ms, fullName);
                continue;
            }
        }

        TShock.Log.ConsoleInfo($"【自动编译】 总共提取到 {ms.Count} 个缺失命名空间:\n {string.Join(", ", ms)}");
        return ms;
    }

    // 辅助方法：添加缺失的命名空间
    private static void AddMissing(List<string> missingList, string name)
    {
        // 尝试提取命名空间部分
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
        {
            var ns = name.Substring(0, lastDot);
            if (!missingList.Contains(ns))
            {
                missingList.Add(ns);
            }
        }
        else if (!missingList.Contains(name))
        {
            missingList.Add(name);
        }
    }
    #endregion

    #region 移除缺失的命名空间
    private static List<SyntaxTree> RemoveUsings(List<SyntaxTree>? trees, List<string> missNs)
    {
        if (trees == null || trees.Count == 0)
            return trees ?? new List<SyntaxTree>();

        var newTrees = new List<SyntaxTree>();
        int total = 0;

        foreach (var tree in trees)
        {
            try
            {
                var root = tree.GetRoot();
                var ToRemove = new List<UsingDirectiveSyntax>();

                // 查找所有using指令
                var Directives = root.DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .ToList();

                // 记录当前文件中找到的命名空间
                var found = new List<string>();

                foreach (var usingDir in Directives)
                {
                    var ns = usingDir.Name?.ToString();
                    if (string.IsNullOrEmpty(ns)) continue;

                    // 检查这个using是否引用了任何一个缺失的命名空间
                    bool Remove = false;
                    foreach (var ms in missNs)
                    {
                        // 情况1: using的命名空间完全等于缺失的命名空间
                        // 情况2: using的命名空间是缺失命名空间的一部分
                        // 情况3: 缺失的命名空间是using命名空间的一部分
                        if (ns == ms ||
                            ns.StartsWith(ms + ".") ||
                            ms.StartsWith(ns + "."))
                        {
                            Remove = true;
                            found.Add(ns);
                            break;
                        }
                    }

                    if (Remove)
                    {
                        ToRemove.Add(usingDir);
                    }
                }

                // 如果有需要移除的using
                if (ToRemove.Count > 0)
                {
                    total += ToRemove.Count;
                    TShock.Log.ConsoleInfo($" 在 {Path.GetFileName(tree.FilePath)} 中移除 {ToRemove.Count} 个using:\n {string.Join(", ", found)}");

                    root = root.RemoveNodes(ToRemove, SyntaxRemoveOptions.KeepNoTrivia);

                    // 创建新的语法树
                    var newTree = CSharpSyntaxTree.ParseText(
                        text: root.ToString(),
                        options: CSharpParseOptions.Default.WithLanguageVersion(Utils.GetLangVer()),
                        path: tree.FilePath,
                        encoding: Encoding.UTF8
                    );

                    newTrees.Add(newTree);
                }
                else
                {
                    newTrees.Add(tree);
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleWarn($"【自动编译】 处理文件 {Path.GetFileName(tree.FilePath)} 失败: {ex.Message}");
                newTrees.Add(tree); // 保留原文件
            }
        }

        TShock.Log.ConsoleInfo($" 总共移除了 {total} 个有问题的using语句");
        return newTrees;
    }
    #endregion
}