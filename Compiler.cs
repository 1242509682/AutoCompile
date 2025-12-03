using System.Diagnostics;
using System.Reflection;
using System.Text;
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
    private class CompilationGroup
    {
        public string Key { get; set; } = string.Empty;  // 根命名空间（分组键）
        public string FullNamespace { get; set; } = string.Empty;  // 完整的命名空间（第一个文件的）
        public List<SyntaxTree> Files { get; set; } = new List<SyntaxTree>();
        public HashSet<string> SubNamespaces { get; set; } = new HashSet<string>();  // 所有子命名空间
        public List<string> PublicClasses { get; set; } = new List<string>();  // 所有公共类
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
            LogError($"编译异常: {ex}");
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
            LogError($"安全检查异常: {ex}");
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

            var trees = new List<SyntaxTree>();
            var refs = new HashSet<string>();
            var skp = new List<string>();
            var err = new List<string>();

            // 添加引用
            AddRefs(refs);

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

                    var code = File.ReadAllText(f, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        skp.Add($"{Path.GetFileName(f)} (空白)");
                        continue;
                    }

                    if (!IsValidCSharpCode(code))
                    {
                        skp.Add($"{Path.GetFileName(f)} (无效)");
                        continue;
                    }

                    var uc = AddUsingsIfNeeded(code);

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

            if (skp.Count > 0 || err.Count > 0)
            {
                LogSkip(skp, err);
            }

            if (trees.Count == 0)
            {
                var msg = "无有效.cs文件";
                if (skp.Count + err.Count > 0)
                {
                    msg += $"，跳过了{skp.Count + err.Count}个文件";
                }
                return CompResult.Fail(msg);
            }

            var rfs = refs
                .Where(File.Exists)
                .Select(r => MetadataReference.CreateFromFile(r))
                .ToList();

            if (rfs.Count == 0)
                return CompResult.Fail("无有效引用");

            var grps = Grouping(trees);
            var dlls = new List<string>();

            var outDir = Path.Combine(Configuration.Paths, "编译输出");

            // 为每个组准备资源
            foreach (var g in grps)
            {
                // 创建编译
                var comp = CSharpCompilation.Create(
                    Utils.CleanName(g.Key),
                    g.Value.Files,
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

                // 添加 .NET 6.0 目标框架特性
                string Framework = @"[assembly: System.Runtime.Versioning.TargetFramework("".NET6.0"", FrameworkDisplayName = "".NET 6.0"")]";

                // 添加语言版本选项，与其他语法树保持一致
                var frameworkTree = CSharpSyntaxTree.ParseText(Framework,
                    options: CSharpParseOptions.Default.WithLanguageVersion(GetLangVer()),
                    encoding: Encoding.UTF8);

                comp = comp.AddSyntaxTrees(frameworkTree);

                // 生成文件名
                string dllName;
                var pn = GetPluginName(g.Value);
                if (!string.IsNullOrEmpty(pn))
                {
                    dllName = $"{Utils.CleanName(pn)}.dll";
                }
                else
                {
                    dllName = $"{Utils.CleanName(g.Key)}.dll";
                }

                var dp = Path.Combine(outDir, dllName);
                var pp = Path.ChangeExtension(dp, ".pdb");

                // 编译
                EmitResult er;

                // 带PDB和资源的编译
                using (var ds = File.Create(dp))
                using (var ps = File.Create(pp))
                {
                    er = comp.Emit(ds, ps);
                }

                if (!er.Success)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"编译错误 [{g.Key}]:");

                    var ers2 = er.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .ToList();

                    foreach (var e in ers2)
                    {
                        var loc = e.Location;
                        if (loc.SourceTree != null)
                        {
                            var ls = loc.GetLineSpan();
                            var fn = Path.GetFileName(loc.SourceTree.FilePath);

                            sb.AppendLine($"  文件: {fn}");
                            sb.AppendLine($"  行: {ls.StartLinePosition.Line + 1}");
                            sb.AppendLine($"  列: {ls.StartLinePosition.Character + 1}");
                            sb.AppendLine($"  错误: {e.GetMessage()}");
                            sb.AppendLine();
                        }
                        else
                        {
                            sb.AppendLine($"  错误: {e.GetMessage()}");
                        }
                    }

                    TShock.Log.Error(sb.ToString());
                    return CompResult.Fail(sb.ToString());
                }

                dlls.Add(dp);

                LogCompile(g.Key, dp, pp);
            }

            try
            {
                var m1 = GC.GetTotalMemory(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var m2 = GC.GetTotalMemory(true);
                var f = m1 - m2;
                if (f > 1024 * 1024)
                {
                    TShock.Log.ConsoleInfo($"【自动编译】 GC释放 {f / 1024 / 1024:F2} MB");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleWarn($"【自动编译】 GC异常: {ex.Message}");
            }

            return CompResult.Success("编译完成", dlls);
        }
        catch (OutOfMemoryException)
        {
            return CompResult.Fail("内存不足");
        }
        catch (Exception ex)
        {
            LogError($"构建异常: {ex}");
            return CompResult.Fail($"构建失败: {ex.Message}");
        }
    }
    #endregion

    #region 为代码添加默认 using
    private static string AddUsingsIfNeeded(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        // 从配置中获取默认 using 指令
        var Default = string.Join("\n", AutoCompile.Config.DefaultUsings) + "\n";

        // 检查代码中是否已经有这些 using（避免重复）
        var existing = ExtractExistingUsings(code);

        // 过滤掉已经存在的 using
        var ToAdd = FilterDuplicateUsings(Default, existing);

        if (string.IsNullOrEmpty(ToAdd))
            return code;

        // 总是添加到文件最开头
        return ToAdd + code;
    }
    #endregion

    #region 提取代码中已有的 using 指令
    private static List<string> ExtractExistingUsings(string code)
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
    private static string FilterDuplicateUsings(string defaultUsings, List<string> existings)
    {
        var lines = defaultUsings.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();

            // 检查是否已经存在相同的 using
            bool exists = existings.Any(existing =>
                string.Equals(existing.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                result.AppendLine(trimmed);
            }
        }

        return result.ToString();
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

            // 如果没有严重错误（则认为无效)
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
            var subNs = group.Value.SubNamespaces;
            TShock.Log.ConsoleInfo($"根命名空间 '{group.Key}': {group.Value.Files.Count} 个文件");
            TShock.Log.ConsoleInfo($"子命名空间: {string.Join(", ", subNs)}");

            foreach (var file in group.Value.Files)
            {
                TShock.Log.ConsoleInfo($"- {Path.GetFileName(file.FilePath)}");
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
            // 获取.NET运行时的系统程序集目录
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
            {
                var Assemblies = new[]
                {
                    // 核心程序集
                     "System.dll",
                     "System.Private.CoreLib.dll",
                     "System.Runtime.dll",
                     "netstandard.dll",

                     // 集合相关
                     "System.Collections.dll",
                     "System.Collections.Concurrent.dll",
                     "System.Collections.Immutable.dll",
                     // LinQ相关
                     "System.Linq.dll",
                     "System.Linq.Expressions.dll",
                     "System.Linq.Queryable.dll",

                     // IO
                     "System.IO.dll",
                     "System.IO.FileSystem.dll",
                     "System.IO.FileSystem.Primitives.dll",

                     // GZip
                     "System.IO.Compression.dll",
                     "System.IO.Compression.ZipFile.dll",

                     // 文本处理
                     "System.Text.RegularExpressions.dll",
                     "System.Text.Encoding.dll",
                     "System.Text.Encoding.Extensions.dll",

                      // 异步和多线程
                     "System.Threading.dll",
                     "System.Threading.Tasks.dll",
                     "System.Threading.Tasks.Extensions.dll",
                     "System.Threading.Thread.dll",
                     "System.Threading.ThreadPool.dll",

                     "System.Runtime.Extensions.dll",
                     "System.Runtime.InteropServices.dll",
                     "System.Runtime.CompilerServices.Unsafe.dll",
                     "System.Runtime.Numerics.dll",
                     "System.ComponentModel.dll",
                     "System.ComponentModel.Primitives.dll",
                     "System.ComponentModel.TypeConverter.dll",
                     "System.Net.Http.dll",
                     "System.Xml.ReaderWriter.dll",
                     "System.Memory.dll",
                     "System.Buffers.dll",
                     "System.Numerics.Vectors.dll",
                     "System.Reflection.dll",
                     "System.Reflection.Primitives.dll",
                     "System.Reflection.Extensions.dll",
                     "System.Reflection.Metadata.dll",
                     "System.Reflection.TypeExtensions.dll",
                     "System.ObjectModel.dll",
                     "System.Globalization.dll",
                     "System.Diagnostics.Debug.dll",
                     "System.Diagnostics.Tools.dll",
                     "System.Diagnostics.Tracing.dll",
                     "System.AppContext.dll",
                     "System.Console.dll",
                     "System.Security.Cryptography.Algorithms.dll",
                     "System.Security.Cryptography.Primitives.dll",
                     "System.Security.Principal.dll",
                };

                foreach (var assembly in Assemblies)
                {
                    var fullPath = Path.Combine(runtimeDir, assembly);
                    if (File.Exists(fullPath) && !refs.Contains(fullPath))
                    {
                        refs.Add(fullPath);
                    }
                }
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
                    try
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
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleWarn($"【自动编译】 处理程序集失败 {Path.GetFileName(dllPath)}: {ex.Message}");
                    }
                }
            }

            if (count > 0)
                TShock.Log.ConsoleInfo($"【自动编译】 从程序集文件夹添加了 {count} 个引用");

            // 2.添加TShockAPI.dll
            var PluginsDir = GetPluginsDir();
            var path2 = Path.Combine(PluginsDir, "TShockAPI.dll");
            if (File.Exists(path2) && !refs.Contains(path2))
            {
                refs.Add(path2);
                TShock.Log.ConsoleInfo($"【自动编译】 添加关键ServerPlugins文件: {"TShockAPI.dll"}");
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
                var binDir = GetBinDir();
                var path3 = Path.Combine(binDir, f);
                if (File.Exists(path3) && !refs.Contains(path3))
                {
                    refs.Add(path3);
                    TShock.Log.ConsoleInfo($"【自动编译】 添加关键bin文件: {f}");
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
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            return assemblyName != null;
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

    #region 路径查找方法
    public static string GetBinDir()
    {
        // 方法1: 从TShockAPI.dll位置推断bin目录
        var tshockPath = typeof(TShockAPI.TShock).Assembly.Location;
        if (!string.IsNullOrEmpty(tshockPath))
        {
            // TShockAPI.dll 在 ServerPlugins 文件夹中
            var PluginsDir = Path.GetDirectoryName(tshockPath);
            if (!string.IsNullOrEmpty(PluginsDir))
            {
                // bin 目录在 serverPlugins 的上级目录
                var parentDir = Directory.GetParent(PluginsDir);
                if (parentDir != null)
                {
                    var binPath = Path.Combine(parentDir.FullName, "bin");
                    if (Directory.Exists(binPath))
                    {
                        TShock.Log.ConsoleInfo($"【自动编译】 找到bin目录: {binPath}");
                        return binPath;
                    }
                }
            }
        }

        // 方法2: 从当前程序集位置推断
        var asmLoc = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrEmpty(asmLoc))
        {
            var dir = Path.GetDirectoryName(asmLoc);
            var cur = new DirectoryInfo(dir);

            while (cur != null)
            {
                var binPath = Path.Combine(cur.FullName, "bin");
                if (Directory.Exists(binPath))
                    return binPath;

                cur = cur.Parent;
            }
        }

        // 如果找不到，使用默认路径
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
    }

    // 获取ServerPlugins目录
    public static string GetPluginsDir()
    {
        var tshockPath = typeof(TShockAPI.TShock).Assembly.Location;

        if (!string.IsNullOrEmpty(tshockPath))
        {
            var PluginsDir = Path.GetDirectoryName(tshockPath);
            if (Directory.Exists(PluginsDir))
            {
                return PluginsDir;
            }
        }

        // 如果找不到，使用默认路径
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ServerPlugins");
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
            _ => LanguageVersion.Latest
        };
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

            TShock.Log.ConsoleInfo(log.ToString());
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录跳过文件日志失败: {ex.Message}");
        }
    }

    private static void LogError(string error)
    {
        try
        {
            TShock.Log.ConsoleError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\n");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录错误日志失败: {ex.Message}");
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

                // 查找继承自TerrariaPlugin的类
                var pluginClass = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(cls => cls.BaseList?.Types
                        .Any(t => t.Type.ToString().Contains("TerrariaPlugin")) == true);

                if (pluginClass != null)
                {
                    // 查找Name属性
                    var nameProp = pluginClass.DescendantNodes()
                        .OfType<PropertyDeclarationSyntax>()
                        .FirstOrDefault(p => p.Identifier.Text == "Name");

                    // 提取插件名称
                    string name = NameFromProperty(nameProp, pluginClass);

                    if (!string.IsNullOrEmpty(name))
                    {
                        TShock.Log.ConsoleInfo($"【自动编译】 在 {Path.GetFileName(tree.FilePath)} 中找到插件: {name}");
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 提取插件信息失败: {ex.Message}");
        }

        return null;
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
}