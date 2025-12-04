using System.Diagnostics;
using System.Reflection;
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
    private static readonly object LockObj = new();
    // 编译组类
    public class CompilationGroup
    {
        public string Key { get; set; } = string.Empty;  // 根命名空间（分组键）
        public string FullNamespace { get; set; } = string.Empty;  // 完整的命名空间（第一个文件的）
        public List<SyntaxTree> Files { get; set; } = new List<SyntaxTree>();
        public HashSet<string> SubNamespaces { get; set; } = new HashSet<string>();  // 所有子命名空间
        public List<string> PublicClasses { get; set; } = new List<string>();  // 所有公共类
    }

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


            var safeResult = CheckSafe(fullPath);
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
                    msg += $"\n生成: {files.Count}个DLL";
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

    #region 安全检查
    private static CompResult CheckSafe(string path)
    {
        try
        {
            var files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);

            // 检查文件数量
            if (files.Length > AutoCompile.Config.MaxFiles)
            {
                return CompResult.Fail($"文件过多: {files.Length} > {AutoCompile.Config.MaxFiles}");
            }

            // 检查文件大小
            long totalSize = 0;
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                totalSize += info.Length;
            }

            var totalMB = totalSize / 1024 / 1024;
            if (totalMB > AutoCompile.Config.MaxSizeMB)
            {
                TShock.Log.ConsoleWarn($"文件过大: {totalMB}MB > {AutoCompile.Config.MaxSizeMB}MB");
                return CompResult.Fail($"文件过大: {totalMB}MB > {AutoCompile.Config.MaxSizeMB}MB");
            }

            return CompResult.Success();
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"安全检查异常: {ex.Message}");
        }
    }
    #endregion

    #region 构建逻辑
    private static CompResult BuildAll(string[] files)
    {
        try
        {
            // 清理旧文件
            Cmd.CleanFiles();

            var trees = new List<SyntaxTree>();  // 语法树列表
            var refs = new HashSet<string>();    // 引用集合
            var skp = new List<string>();        // 跳过文件列表
            var err = new List<string>();        // 错误文件列表

            // 添加引用
            AddRefs(refs);

            // 遍历所有文件
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);    // 文件信息
                    if (fi.Length == 0)          // 空文件
                    {
                        skp.Add($"{Path.GetFileName(f)} (空)");
                        continue;
                    }

                    var code = Utils.ReadAndFixFile(f);

                    // 直接移除有问题的 using 语句
                    code = RemoveUsings(code);

                    if (string.IsNullOrWhiteSpace(code))  // 空白文件
                    {
                        skp.Add($"{Path.GetFileName(f)} (空白)");
                        continue;
                    }

                    if (!IsValidCSharpCode(code))  // 无效C#代码
                    {
                        skp.Add($"{Path.GetFileName(f)} (无效)");
                        continue;
                    }

                    var uc = AddUsings(code);  // 添加using语句

                    // 解析语法树
                    var tree = CSharpSyntaxTree.ParseText(
                        text: uc,
                        options: CSharpParseOptions.Default.WithLanguageVersion(GetLangVer()),
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
                LogSkip(skp, err);
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

            // 分组编译
            var grps = Grouping(trees);
            var dlls = new List<string>();

            var outDir = Path.Combine(Configuration.Paths, "编译输出");

            // 遍历每个分组
            foreach (var g in grps)
            {
                // 创建编译
                var comp = CSharpCompilation.Create(
                    Utils.CleanName(g.Key),  // 清理后的名称
                    g.Value.Files,            // 分组文件
                    rfs,                      // 引用
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

                // 添加目标框架特性 .NET 6.0
                string fw = @"[assembly: System.Runtime.Versioning.TargetFramework("".NET6.0"", FrameworkDisplayName = "".NET 6.0"")]";

                // 创建框架特性语法树
                var fwTree = CSharpSyntaxTree.ParseText(fw,
                    options: CSharpParseOptions.Default.WithLanguageVersion(GetLangVer()),
                    encoding: Encoding.UTF8);

                comp = comp.AddSyntaxTrees(fwTree);  // 添加到编译

                // 生成DLL文件名
                string dllName;
                var pName = GetPluginName(g.Value);
                dllName = $"{Utils.CleanName(pName)}.dll";

                var dllPath = Path.Combine(outDir, dllName);
                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

                // 编译
                EmitResult er;

                // 带PDB输出
                using (var dStream = File.Create(dllPath))
                using (var pStream = File.Create(pdbPath))
                {
                    er = comp.Emit(dStream, pStream);
                }

                // 编译失败处理
                if (!er.Success)
                {
                    return ErrorMessMag.ErrorMess(g, er);
                }

                // 编译成功，添加到结果列表
                dlls.Add(dllPath);

                // 记录编译日志
                LogCompile(g.Key, dllPath, pdbPath);
            }

            // 编译成功后清理所有编译日志文件
            ClearLogs();

            return CompResult.Success("编译完成", dlls);
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
            // 无论成功失败 只要结束就尝试进行垃圾回收
            try
            {
                var mem1 = GC.GetTotalMemory(false);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                var mem2 = GC.GetTotalMemory(true);
                var freed = mem1 - mem2;
                if (freed > 1024 * 1024)  // 释放超过1MB
                {
                    TShock.Log.ConsoleInfo($"【自动编译】GC释放内存 {freed / 1024 / 1024:F2} MB");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleWarn($"【自动编译】内存清理异常: {ex.Message}");
            }
        }
    }
    #endregion

    #region 编译成功后清理日志文件
    private static void ClearLogs()
    {
        // 检查配置是否启用清理
        if (!AutoCompile.Config.ClearLogs) return;

        try
        {
            var logDir = Path.Combine(Configuration.Paths, "编译日志");
            if (!Directory.Exists(logDir))
                return;

            // 获取所有日志文件
            var logFiles = Directory.GetFiles(logDir, "*.txt", SearchOption.AllDirectories);
            if (logFiles.Length == 0)
                return;

            int count = 0;
            foreach (var logFile in logFiles)
            {
                File.Delete(logFile);
                count++;
            }

            if (count > 0)
            {
                TShock.Log.ConsoleInfo($"【自动编译】 清理了 {count} 个编译日志文件");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 清理编译日志失败: {ex.Message}");
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

    #region 移除指定Using语句
    private static string RemoveUsings(string code)
    {
        var rm = AutoCompile.Config.RemoveUsings;
        if (rm == null || rm.Count == 0) return code;

        // 简单的移除逻辑：直接替换为空
        foreach (var to in rm)
        {
            if (string.IsNullOrWhiteSpace(to))
                continue;

            // 精确移除整行
            string pattern = @"^\s*" + Regex.Escape(to.Trim()) + @"\s*\r?\n";
            code = Regex.Replace(code, pattern, "", RegexOptions.Multiline);

            // 如果是文件最后一行的情况
            pattern = @"^\s*" + Regex.Escape(to.Trim()) + @"\s*$";
            code = Regex.Replace(code, pattern, "", RegexOptions.Multiline);
        }

        return code;
    }
    #endregion

    #region 获取C#版本号 .net 6对应的是CSharp11
    private static LanguageVersion GetLangVer()
    {
        return AutoCompile.Config.LangVer switch
        {
            "CSharp8" => LanguageVersion.CSharp8,
            "CSharp9" => LanguageVersion.CSharp9,
            "CSharp10" => LanguageVersion.CSharp10,
            "CSharp11" => LanguageVersion.CSharp11,
            "CSharp12" => LanguageVersion.CSharp12,
            "CSharp13" => LanguageVersion.CSharp13,
            "CSharp14" => LanguageVersion.CSharp14,
            _ => LanguageVersion.Latest
        };
    }
    #endregion

    #region 检查是否是C#代码 
    private static bool IsValidCSharpCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            // 尝试解析，让编译器决定是否有效
            var tree = CSharpSyntaxTree.ParseText(code);

            // 如果有语法错误，ParseText可能不会抛出异常
            // 但会包含诊断信息
            var diagnostics = tree.GetDiagnostics();

            // 如果没有严重错误（则认为可用)
            return !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }
    #endregion

    #region 按命名空间分组（支持根命名空间）
    private static Dictionary<string, CompilationGroup> Grouping(List<SyntaxTree> trees)
    {
        var groups = new Dictionary<string, CompilationGroup>();

        foreach (var tree in trees)
        {
            try
            {
                var root = tree.GetRoot();

                // 获取完整命名空间
                string ns = "Global";

                // 查找标准命名空间声明
                var nsDecl = root.DescendantNodes()
                    .OfType<NamespaceDeclarationSyntax>()
                    .FirstOrDefault();

                if (nsDecl != null)
                {
                    ns = nsDecl.Name.ToString();
                }
                else
                {
                    // 查找文件作用域命名空间声明
                    var fileNs = root.DescendantNodes()
                        .OfType<FileScopedNamespaceDeclarationSyntax>()
                        .FirstOrDefault();

                    if (fileNs != null)
                    {
                        ns = fileNs.Name.ToString();
                    }
                }

                // 计算分组键：使用根命名空间（第一个点之前的部分）
                string key = GetRootNamespace(ns);

                // 获取文件中定义的所有公共类名
                var pubClasses = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(c => c.Modifiers.Any(m => m.Text == "public" || m.Text == "internal"))
                    .Select(c => c.Identifier.Text)
                    .ToList();

                // 创建或获取分组
                if (!groups.ContainsKey(key))
                {
                    groups[key] = new CompilationGroup
                    {
                        Key = key,
                        FullNamespace = ns,
                        Files = new List<SyntaxTree>(),
                        SubNamespaces = new HashSet<string>(),
                        PublicClasses = new List<string>()
                    };
                }

                // 添加文件到分组
                groups[key].Files.Add(tree);
                groups[key].SubNamespaces.Add(ns);
                groups[key].PublicClasses.AddRange(pubClasses);
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"【自动编译】 分析文件 {Path.GetFileName(tree.FilePath)} 时出错: {ex.Message}");
            }
        }

        // 输出分组信息
        TShock.Log.ConsoleInfo($"【自动编译】 分组情况 (共{groups.Count}组):");
        foreach (var group in groups)
        {
            TShock.Log.ConsoleInfo($"【命名空间】 '{group.Key}': {group.Value.Files.Count} 个文件");
            // 添加索引，从1开始
            int idx = 1;
            foreach (var file in group.Value.Files)
            {
                TShock.Log.ConsoleInfo($"{idx}.{Path.GetFileName(file.FilePath)}");
                idx++;
            }
        }

        return groups;
    }

    // 获取根命名空间（第一个点之前的部分）
    private static string GetRootNamespace(string fullNamespace)
    {
        if (string.IsNullOrEmpty(fullNamespace) || fullNamespace == "Global")
            return "Global";

        var dotIndex = fullNamespace.IndexOf('.');
        return dotIndex > 0 ? fullNamespace.Substring(0, dotIndex) : fullNamespace;
    }
    #endregion

    #region 添加程序集引用
    internal static void AddRefs(HashSet<string> refs)
    {
        try
        {
            TShock.Log.ConsoleInfo("【自动编译】 开始添加引用...");

            // 1. 添加TS程序集引用
            AddTShockReferences(refs);

            // 2. 系统程序集 - 添加更多基础程序集
            AddSystemReferences(refs);

            TShock.Log.ConsoleInfo($"【自动编译】 总共添加了 {refs.Count} 个引用");
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
                        if (IsValidAssembly(dllPath))
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

    #region 检查程序集是否有效
    private static bool IsValidAssembly(string assemblyPath)
    {
        try
        {
            // 尝试加载程序集来验证其有效性
            var name = AssemblyName.GetAssemblyName(assemblyPath);
            return name != null;
        }
        catch (BadImageFormatException)
        {
            // 不是有效的.NET程序集
            return false;
        }
        catch (FileLoadException)
        {
            // 文件损坏或无法加载
            return false;
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 检查程序集失败 {Path.GetFileName(assemblyPath)}: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region 提取插件名称
    private static string GetPluginName(CompilationGroup group)
    {
        try
        {
            // 遍历组中的所有文件，查找插件信息
            foreach (var tree in group.Files)
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

    #region 日志方法
    private static void LogCompile(string ns, string dllPath, string pdbPath)
    {
        try
        {
            var log = new StringBuilder();
            log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            log.AppendLine($"命名空间: {ns}");
            log.AppendLine($"DLL文件: {Path.GetFileName(dllPath)}");
            log.AppendLine($"文件大小: {new FileInfo(dllPath).Length / 1024} KB");

            if (pdbPath != null && File.Exists(pdbPath))
            {
                log.AppendLine($"PDB文件: {Path.GetFileName(pdbPath)}");
            }

            log.AppendLine();

            TShock.Log.Debug(log.ToString());
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录编译日志失败: {ex.Message}");
        }
    }

    private static void LogSkip(List<string> skipped, List<string> errors)
    {
        try
        {
            var log = new StringBuilder();
            log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            if (skipped.Count > 0)
            {
                log.AppendLine($"跳过 {skipped.Count} 个文件:");
                foreach (var file in skipped.Take(10))
                    log.AppendLine($"  {file}");

                if (skipped.Count > 10)
                    log.AppendLine($"  ... 等{skipped.Count}个文件");
            }

            if (errors.Count > 0)
            {
                log.AppendLine($"错误 {errors.Count} 个文件:");
                foreach (var file in errors.Take(10))
                    log.AppendLine($"  {file}");

                if (errors.Count > 10)
                    log.AppendLine($"  ... 等{errors.Count}个文件");
            }

            log.AppendLine();

            TShock.Log.Info(log.ToString());
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录跳过文件日志失败: {ex.Message}");
        }
    }
    #endregion
}