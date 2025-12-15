using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TShockAPI;
using static AutoCompile.AutoCompile;

namespace AutoCompile;

public class ScriptExec : IDisposable
{
    private readonly Dictionary<string, ScriptRunner<object>> cache; // 脚本缓存
    private readonly Dictionary<string, string> hashMap; // 脚本哈希表(检查是否修改用于判断重新编译)
    private readonly object lockObj = new();
    private bool isDisposed = false; // 是否已释放
    private readonly Type global; // 编写脚本时用全局变量类

    public ScriptExec(Type globals = null)
    {
        cache = new Dictionary<string, ScriptRunner<object>>(StringComparer.OrdinalIgnoreCase);
        hashMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        this.global = globals;
    }

    #region 预编译脚本方法
    public CompResult PreCompile(string name, string code, List<string> usings)
    {
        try
        {
            lock (lockObj)
            {
                // 计算哈希
                var newHash = Utils.CalcHash(code, usings);

                // 检查是否已缓存
                if (cache.ContainsKey(name) &&
                    hashMap.TryGetValue(name, out var oldHash) &&
                    oldHash == newHash)
                    return CompResult.Success("已缓存");

                // 清理旧的缓存
                if (cache.ContainsKey(name)) cache.Remove(name);

                // 添加额外的 usings
                if (usings?.Count > 0)
                {
                    // 获取当前代码中已有的 using
                    var existing = Utils.GetExistUsings(code);

                    // 过滤掉已经存在的 using
                    var fmtUsgs = Utils.FmtUsings(usings);
                    var toAdd = Utils.FilterUsings(fmtUsgs, existing);

                    if (!string.IsNullOrEmpty(toAdd))
                        code = toAdd + code;
                }

                // 创建脚本
                var options = CreateOptions();

                Script<object> script = global == null
                ? CSharpScript.Create<object>(code, options)
                : CSharpScript.Create<object>(code, options, global);

                // 检查编译错误
                var error = script.GetCompilation()
                                   .GetDiagnostics()
                                   .Where(d => d.Severity == DiagnosticSeverity.Error)
                                   .ToList();

                if (error.Count > 0)
                {
                    return Compiler.ErrorScript(name, error);
                }

                // 创建运行器并保存哈希
                cache[name] = script.CreateDelegate();
                hashMap[name] = newHash;
                return CompResult.Success("编译成功");
            }
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"编译异常: {ex.Message}");
        }
        finally
        {
            Compiler.ClearMetaRefs(); // 清理编译元数据缓存
        }
    }
    #endregion

    #region 重新编译方法
    public CompResult Recompile(string name, string code, List<string> usings)
    {
        lock (lockObj)
        {
            if (cache.ContainsKey(name))
                cache.Remove(name);

            if (hashMap.ContainsKey(name))
                hashMap.Remove(name);

            return PreCompile(name, code, usings);
        }
    }
    #endregion

    #region 执行器
    // 异步执行脚本
    public async Task<CompResult> AsyncRun(string name, object globals, int timeout = 5000)
    {
        try
        {
            ScriptRunner<object>? runner;

            lock (lockObj)
            {
                if (!cache.TryGetValue(name, out runner))
                    return CompResult.Fail($"脚本未编译: {name}");
            }

            var task = runner(globals);
            if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            {
                var result = await task;
                return CompResult.Success(result?.ToString()!);
            }

            return CompResult.Fail($"执行超时 ({timeout}ms)");
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"执行异常: {ex.Message}");
        }
    }

    // 同步执行脚本
    public CompResult SyncRun(string name, object globals)
    {
        try
        {
            ScriptRunner<object>? runner;

            lock (lockObj)
            {
                if (!cache.TryGetValue(name, out runner))
                    return CompResult.Fail($"脚本未编译: {name}");
            }

            var task = runner(globals);
            var awaiter = task.GetAwaiter();
            var result = awaiter.GetResult();
            return CompResult.Success(result?.ToString()!);
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"执行异常: {ex.Message}");
        }
    }
    #endregion

    #region 创建脚本选项
    private ScriptOptions CreateOptions()
    {
        // 默认导入
        var imports = new List<string>();
        var refs = Compiler.GetMetaRefs();

        try
        {
            if (Config.Usings != null)
            {
                imports.AddRange(Config.Usings);
            }

            if (global != null && !string.IsNullOrEmpty(global.Namespace))
            {
                imports.Add(global.Namespace);
            }

            return ScriptOptions.Default
                .WithReferences(refs)
                .WithImports(imports)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithAllowUnsafe(true)
                .WithEmitDebugInformation(false)  // 禁用调试信息以减少内存
                .WithCheckOverflow(false);        // 禁用溢出检查提升性能
        }
        finally
        {
            Compiler.ClearMetaRefs();
            imports.Clear();
            refs.Clear();
        }
    }
    #endregion

    #region 批量预编译（分阶段进度）
    public CompResult BatchCompile(string csxDir, List<string> usings)
    {
        try
        {
            if (!Directory.Exists(csxDir))
                return CompResult.Fail($"脚本目录不存在: {csxDir}");

            var files = Directory.GetFiles(csxDir, "*.csx", SearchOption.AllDirectories);
            int total = files.Length;

            if (total == 0)
            {
                TShock.Log.ConsoleInfo("\n[自动编译] 未找到脚本文件");
                return CompResult.Success("没有可编译的脚本");
            }

            TShock.Log.ConsoleInfo($"\n[自动编译] 开始批量编译 {total} 个脚本");

            // 阶段1：加载文件
            TShock.Log.ConsoleInfo($"[自动编译] 阶段1: 加载脚本文件...");
            var scripts = new Dictionary<string, string>();
            int load = 0;

            foreach (var filePath in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                try
                {
                    var code = File.ReadAllText(filePath, Encoding.UTF8);
                    scripts[fileName] = code;
                    load++;

                    // 显示加载进度
                    if (load % 10 == 0 || load == total)
                    {
                        double tage = (double)load / total * 100;
                        Console.Write($"\r[自动编译] 加载进度: {tage:F1}% ({load}/{total})");
                    }
                }
                catch (Exception ex)
                {
                    TShock.Log.ConsoleError($"[自动编译] 加载文件失败: {fileName} - {ex.Message}");
                }
            }
            Console.WriteLine(); // 换行

            // 阶段2：编译脚本
            TShock.Log.ConsoleInfo($"[自动编译] 阶段2: 编译脚本...");
            int comp = 0;
            int yes = 0, no = 0, ce = 0;
            var errors = new List<string>();

            foreach (var script in scripts)
            {
                comp++;

                // 统一计算百分比
                double tage = (double)comp / total * 100;

                try
                {
                    CompResult result = PreCompile(script.Key, script.Value, usings);
                    Console.Write($"\r[自动编译] 编译进度: {tage:F1}% ({comp}/{total})");

                    if (result.Ok)
                    {
                        if (result.Msg == "已缓存")
                            ce++;
                        else
                            yes++;
                    }
                    else
                    {
                        no++;
                        errors.Add($"[{script.Key}] {result.Msg}");
                    }
                }
                catch (Exception ex)
                {
                    no++;
                    errors.Add($"[{script.Key}] 异常: {ex.Message}");
                    Console.Write($"\r[自动编译] 编译进度: {tage:F1}% ({comp}/{total})");
                }
            }
            Console.WriteLine(); // 换行

            // 阶段3：总结报告
            TShock.Log.ConsoleInfo($"[自动编译] 阶段3: 生成报告...");

            // 漂亮的总结表格
            Console.WriteLine("╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("║                [自动编译] 编译结果报告               ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  总计脚本: {total,-4}个                                    ║");
            Console.WriteLine($"║  编译成功: {yes,-4}个                                    ║");
            Console.WriteLine($"║  使用缓存: {ce,-4}个                                    ║");
            Console.WriteLine($"║  编译失败: {no,-4}个                                    ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════╝");

            // 错误详情
            if (errors.Count > 0)
            {
                TShock.Log.ConsoleError("\n[自动编译] 错误详情:");
                for (int i = 0; i < Math.Min(errors.Count, 5); i++)
                {
                    TShock.Log.ConsoleError($"  {i + 1}. {errors[i]}");
                }
                if (errors.Count > 5)
                {
                    TShock.Log.ConsoleError($"  ... 还有 {errors.Count - 5} 个错误");
                }
            }

            TShock.Log.ConsoleInfo($"[自动编译] 脚本目录: {csxDir}");

            return no > 0
                ? CompResult.Fail($"有{no}个脚本编译失败")
                : CompResult.Success("批量编译完成");
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"批量编译异常: {ex.Message}");
        }
        finally
        {
            Compiler.ClearMetaRefs();
            GC.Collect(2, GCCollectionMode.Default);
        }
    }
    #endregion

    #region 释放方法
    public void Dispose()
    {
        if (!isDisposed)
        {
            lock (lockObj)
            {
                // 彻底清空缓存
                if (cache != null)
                {
                    cache.Clear();
                }

                if (hashMap != null)
                {
                    hashMap.Clear();
                }
            }

            // 强制清理编译器资源
            Compiler.ClearMetaRefs();

            isDisposed = true;
        }

        GC.SuppressFinalize(this);
    }
    #endregion
}