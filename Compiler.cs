using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Terraria;
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

public class Compiler
{
    public static readonly object LockObj = new();

    // 静态构造函数，只会执行一次
    static Compiler()
    {
        try
        {
            // 修复中文编码用的
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
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
                var msg = $"已经编译{csFiles.Length}个cs文件 用时:{sw.ElapsedMilliseconds}ms";
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

    #region 构建逻辑（核心方法）
    private static CompResult BuildAll(string[] files)
    {
        // 使用局部变量，让它们尽早离开作用域
        List<SyntaxTree>? trees = null;
        List<string>? skp = null;
        List<string>? err = null;
        List<MetadataReference> rfs = null;

        try
        {
            Utils.CleanOutFiles(); // 清理旧文件
            skp = new List<string>();
            err = new List<string>();
            trees = new List<SyntaxTree>();

            TShock.Log.ConsoleInfo("【自动编译】 开始添加引用...");

            rfs = GetMetaRefs(true);
            if (rfs.Count == 0) return CompResult.Fail("无有效引用");

            TShock.Log.ConsoleInfo($"【自动编译】 已加载 {rfs.Count} 个引用");

            int total = files.Length;
            int proc = 0;

            TShock.Log.ConsoleInfo($"【自动编译】 开始处理 {total} 个源文件...");

            // 遍历所有文件
            foreach (var f in files)
            {
                proc++;

                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length == 0)
                    {
                        skp.Add($"{Path.GetFileName(f)} (空)");
                        continue;
                    }

                    var code = Utils.ReadAndFixFile(f);
                    code = RemoveUsings(code);

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

                    // 显示处理进度（每10%显示一次，或者每个文件都显示）
                    double tage = (double)proc / total * 100;

                    // 显示进度条
                    DisplayProgress("解析源码", proc, total, tage);
                }
                catch (Exception ex)
                {
                    err.Add($"{Path.GetFileName(f)}: {ex.Message}");
                    TShock.Log.ConsoleError($"【自动编译】 解析 {Path.GetFileName(f)} 失败: {ex.Message}");

                    // 出错时也显示进度
                    double tage = (double)proc / total * 100;
                    DisplayProgress("解析源码", proc, total, tage);
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

            TShock.Log.ConsoleInfo($"【自动编译】 解析完成，共 {trees.Count} 个有效文件");
            TShock.Log.ConsoleInfo("【自动编译】 开始编译生成DLL...");

            // 获取插件名称
            var pName = Utils.GetPluginName(trees);
            if (string.IsNullOrEmpty(pName)) pName = "MyPlugin";
            var outDir = Path.Combine(Configuration.Paths, "编译输出");
            var dllName = $"{Utils.CleanName(pName)}.dll";
            var dllPath = Path.Combine(outDir, dllName);
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");

            // 显示编译进度
            TShock.Log.ConsoleInfo($"【自动编译】 正在编译: {pName}");

            EmitResult er = CreateComp(trees, rfs, pName, dllPath, pdbPath);

            // 编译失败处理
            if (!er.Success)
            {
                // 返回错误信息
                return ErrorMess(pName, er);
            }

            LogsMag.LogCompile(pName, dllPath, pdbPath);
            Utils.ClearLogs(); // 成功后清理日志

            // 显示成功信息
            TShock.Log.ConsoleInfo($"【自动编译】 编译完成: {pName}");
            TShock.Log.ConsoleInfo($"【自动编译】 DLL路径: {dllPath}");

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
            ClearMem(trees, skp, err, rfs);  // 清理内存
            ClearMetaRefs(); // 清理编译元数据缓存
        }
    }
    #endregion

    #region 显示进度条
    public static void DisplayProgress(string stage, int curr, int total, double tage)
    {
        // 每10%显示一次，或者每个文件都显示（根据总数决定）
        bool Display = false;

        if (total <= 10)
        {
            // 文件少时每个都显示
            Display = true;
        }
        else if (total <= 50)
        {
            // 每处理10%显示一次
            int step = Math.Max(1, total / 10);
            Display = curr % step == 0 || curr == total;
        }
        else
        {
            // 文件多时每处理5%显示一次
            int step = Math.Max(1, total / 20);
            Display = curr % step == 0 || curr == total;
        }

        if (Display)
        {
            // 进度条长度
            int barWidth = 20;
            int progWidth = (int)(barWidth * tage / 100);
            string progBar = new string('█', progWidth) +
                                 new string('░', barWidth - progWidth);

            // 在同一行显示进度
            Console.Write($"\r【自动编译】 {stage}: [{progBar}] {tage:F1}% ({curr}/{total})");

            // 如果是最后一个文件，换行
            if (curr == total)
            {
                Console.WriteLine();
            }
        }
    }
    #endregion

    #region 为代码添加默认 using
    public static string AddUsings(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return code;

        // 从配置中获取默认 using 指令
        var defList = AutoCompile.Config.Usings;
        // 从配置获取并格式化
        var fmtUsgs = Utils.FmtUsings(defList);

        if (string.IsNullOrEmpty(fmtUsgs))
            return code;

        // 检查代码中是否已经有这些 using（避免重复）
        var existing = Utils.GetExistUsings(code);

        // 过滤掉已经存在的 using
        var ToAdd = Utils.FilterUsings(fmtUsgs, existing);

        if (string.IsNullOrEmpty(ToAdd))
            return code;

        // 总是添加到文件最开头
        return ToAdd + code;
    }
    #endregion

    #region 移除指定Using语句
    public static string RemoveUsings(string code)
    {
        var rm = AutoCompile.Config.RemoveUsings;
        if (rm == null || rm.Count == 0) return code;

        // 简单的移除逻辑：直接替换为空
        foreach (var to in rm)
        {
            if (string.IsNullOrWhiteSpace(to))
                continue;

            // 移除带using关键字的完整语句
            string pattern1 = @"^\s*using\s+" + Regex.Escape(to.Trim()) + @"\s*;\s*\r?\n";
            code = Regex.Replace(code, pattern1, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            // 移除不带using关键字的命名空间（可能在已有的using语句中）
            string pattern = @"^\s*" + Regex.Escape(to.Trim()) + @"\s*\r?\n";
            code = Regex.Replace(code, pattern, "", RegexOptions.Multiline);

            // 移除文件最后一行的情况
            pattern = @"^\s*" + Regex.Escape(to.Trim()) + @"\s*$";
            code = Regex.Replace(code, pattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        return code;
    }
    #endregion

    #region 添加系统运行时程序集
    public static void AddSystemReferences(HashSet<string> refs)
    {
        try
        {
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
                    }
                    else
                    {
                        TShock.Log.ConsoleError($"【自动编译】 文件不存在 {file} ");
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
    public static void AddTShockReferences(HashSet<string> refs, bool dll)
    {
        try
        {
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
                        }
                        else
                        {
                            TShock.Log.ConsoleWarn($"【自动编译】 跳过无效的程序集: {Path.GetFileName(dllPath)}");
                        }
                    }
                }
            }

            // 2.添加 ServerPlugins 文件夹所有DLL（仅当前文件夹，不扫描子文件夹）
            var PluginsDir = Path.Combine(typeof(TShock).Assembly.Location, "ServerPlugins");
            if (dll)
            {
                // 如果是编译插件，只添加 TShockAPI.dll（避免文件夹里存在相同插件导致引用错乱）
                var path2 = Path.Combine(PluginsDir, "TShockAPI.dll");
                if (File.Exists(path2) && !refs.Contains(path2))
                {
                    refs.Add(path2);
                }
            }
            else
            {
                // 否则编译的是C#脚本,添加所有DLL（方便编写时引用插件本身）
                var dllFiles2 = Directory.GetFiles(PluginsDir, "*.dll", SearchOption.TopDirectoryOnly);
                foreach (var path2 in dllFiles2)
                {
                    if (File.Exists(path2) && !refs.Contains(path2))
                    {
                        refs.Add(path2);
                    }
                }
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

    #region 创建编译
    private static EmitResult CreateComp(List<SyntaxTree>? trees,
        List<MetadataReference> rfs,
        string pluginName, string dllPath, string pdbPath)
    {
        try
        {
            // 创建编译
            var comp = CSharpCompilation.Create(
                Utils.CleanName(pluginName),
                trees,
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
            using (var dStream = File.Create(dllPath))
            using (var pStream = File.Create(pdbPath))
            {
                EmitResult er = comp.Emit(dStream, pStream);
                return er;
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 编译异常: {ex.Message}");
            return null;
        }
    }
    #endregion

    #region 错误处理
    public static CompResult ErrorMess(string pluginName, EmitResult er)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n❌ 编译失败 [{pluginName}]");
            sb.AppendLine("-".PadRight(40, '-'));

            // 获取错误
            var errs = er.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            // 按文件分组显示错误
            var ByFile = errs
                .GroupBy(err => Utils.GetFileName(err))
                .OrderBy(g => g.Key)
                .ToList();

            sb.AppendLine($" 发现 {errs.Count} 个错误，分布在 {ByFile.Count} 个文件中:");

            // 只显示文件名和错误数量
            foreach (var group in ByFile)
            {
                var name = group.Key;
                var count = group.Count();

                sb.AppendLine($" 📁 {name} - {count}个错误");
            }

            // 记录到控制台
            TShock.Log.ConsoleError(sb.ToString());

            // 记录到日志文件
            LogsMag.LogErrFile(pluginName, errs);

            return CompResult.Fail("编译失败");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"❌ 编译失败 [{pluginName}]");
            TShock.Log.ConsoleError($"错误异常: {ex.Message}");
            return CompResult.Fail("编译失败");
        }
    }
    #endregion

    #region 脚本编译错误处理
    public static CompResult ErrorScript(string scriptName, List<Diagnostic> errors)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n❌ 脚本编译失败 [{scriptName}]");
            sb.AppendLine("-".PadRight(40, '-'));
            var errs = errors.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

            // 按错误类型分组显示
            var ByFile = errs
                .GroupBy(err => Utils.GetFileName(err))
                .OrderBy(g => g.Key)
                .ToList();

            sb.AppendLine($" 发现 {errs.Count} 个错误，分布在 {ByFile.Count} 个文件中:");

            foreach (var group in ByFile)
            {
                var name = group.Key;
                var count = group.Count();
                sb.AppendLine($" 📁 {name} - {count}个错误");
            }

            TShock.Log.ConsoleError(sb.ToString());   // 记录到控制台
            LogsMag.LogErrFile(scriptName, errs); // 记录到日志文件
            return CompResult.Fail($"脚本编译失败，共{errs.Count}个错误,请查看《自动编译》-《编译日志》");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"❌ 脚本编译失败 [{scriptName}]");
            TShock.Log.ConsoleError($"错误异常: {ex.Message}");
            return CompResult.Fail("脚本编译失败");
        }
    }
    #endregion

    #region 结束编译清理内存
    private static void ClearMem(List<SyntaxTree>? trees, List<string>? skp, List<string>? err, List<MetadataReference>? rfs)
    {
        try
        {
            // 1.清理集合，让它们可以被GC
            trees?.Clear();
            skp?.Clear();
            err?.Clear();

            // 2. 释放 MetadataReference
            if (rfs != null)
            {
                foreach (var rf in rfs)
                {
                    if (rf is IDisposable disposable)
                        disposable.Dispose();
                }

                rfs.Clear();
            }

            // 2. 分步GC
            long before = GC.GetTotalMemory(false);

            // 清理第0代和第1代
            GC.Collect(0, GCCollectionMode.Forced);
            GC.Collect(1, GCCollectionMode.Forced);

            // 等待一会儿
            Thread.Sleep(50);

            // 清理第2代（完整GC）
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();

            long after = GC.GetTotalMemory(true);
            long freed = before - after;

            if (freed > 1024 * 1024)
            {
                TShock.Log.ConsoleInfo($"【内存清理】 释放了 {freed / 1024 / 1024:F2} MB");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 内存清理异常: {ex.Message}");
        }
    }
    #endregion

    #region 获取元数据引用
    private static List<MetadataReference> metaRefs; // 缓存元数据引用
    public static List<MetadataReference> GetMetaRefs(bool dll = false)
    {
        lock (LockObj)
        {
            if (metaRefs == null)
            {
                var refs = new HashSet<string>();
                AddTShockReferences(refs, dll);
                AddSystemReferences(refs);
                var abRefs = new List<string>();
                foreach (var r in refs)
                {
                    try
                    {
                        var Paths = Path.GetFullPath(r);
                        if (File.Exists(Paths))
                        {
                            abRefs.Add(Paths);
                        }
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleWarn($"无法将路径转换为绝对路径，跳过: {r}, 错误: {ex.Message}");
                    }
                }

                metaRefs = abRefs.Select(r => (MetadataReference)MetadataReference.CreateFromFile(r)).ToList();
            }
            return metaRefs;
        }
    }
    #endregion

    #region 清除元数据引用缓存
    public static void ClearMetaRefs()
    {
        lock (LockObj)
        {
            if (metaRefs != null)
            {
                // 显式释放每个MetadataReference
                foreach (var metaRef in metaRefs)
                {
                    // MetadataReference没有Dispose，但可清除引用链
                    // 对于非托管资源，确保释放
                    if (metaRef is IDisposable disposable)
                        disposable.Dispose();
                }

                metaRefs.Clear();
                metaRefs = null;
            }

            // 分代清理策略
            GC.Collect(0, GCCollectionMode.Forced);
            Thread.Sleep(10);
            GC.Collect(1, GCCollectionMode.Forced);
            Thread.Sleep(10);
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
        }
    }
    #endregion
}