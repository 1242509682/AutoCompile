using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TShockAPI;

namespace AutoCompile;

public class ScriptExec : IDisposable
{
    private readonly Dictionary<string, ScriptRunner<object>> cache; // 脚本缓存
    private readonly object lockObj = new();
    private bool isDisposed = false; // 是否已释放
    private readonly List<string> ExtraAsse; // 额外程序集
    private readonly Type global; // 脚本可用的全局变量类

    public ScriptExec(Type globals = null, List<string> extra = null)
    {
        cache = new Dictionary<string, ScriptRunner<object>>(StringComparer.OrdinalIgnoreCase);
        this.ExtraAsse = extra ?? new List<string>();
        this.global = globals;

        if (globals != null)
        {
            var assemblyPath = globals.Assembly.Location;
            if (File.Exists(assemblyPath) && !this.ExtraAsse.Contains(assemblyPath))
            {
                this.ExtraAsse.Add(assemblyPath);
            }
        }
    }

    #region 预编译脚本方法
    public CompResult PreCompile(string name, string code, List<string> usings)
    {
        try
        {
            lock (lockObj)
            {
                if (cache.ContainsKey(name))
                    return CompResult.Success("已缓存");

                // 使用 Compiler 的默认 using
                code = Compiler.AddUsings(code);
                code = Compiler.RemoveUsings(code);

                // 添加额外的 usings
                if (usings?.Count > 0)
                {
                    var fmtUsgs = Utils.FmtUsings(usings);
                    if (!string.IsNullOrEmpty(fmtUsgs))
                        code = fmtUsgs + code;
                }

                // 创建脚本
                var options = CreateOptions();

                Script<object> script;

                if (global == null)
                {
                    script = CSharpScript.Create<object>(code, options);
                }
                else
                {
                    script = CSharpScript.Create<object>(code, options, global);
                }

                // 检查编译错误
                var comp = script.GetCompilation();
                var errors = comp.GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                if (errors.Count > 0)
                {
                    return Compiler.ErrorScript(name, errors);
                }

                // 创建运行器（使用RunAsync来获取ScriptRunner）
                cache[name] = script.CreateDelegate();
                return CompResult.Success("编译成功");
            }
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"编译异常: {ex.Message}");
        }
    }
    #endregion

    #region 执行器
    // 异步执行脚本
    public async Task<CompResult> AsyncRun(string name, object globals, int timeout = 5000)
    {
        try
        {
            ScriptRunner<object> runner;

            lock (lockObj)
            {
                if (!cache.TryGetValue(name, out runner))
                    return CompResult.Fail($"脚本未编译: {name}");
            }

            var task = runner(globals);
            if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            {
                var result = await task;
                return CompResult.Success(result?.ToString());
            }

            return CompResult.Fail($"执行超时 ({timeout}ms)");
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"执行异常: {ex.Message}");
        }
    }

    // 同步执行脚本
    public CompResult SyncRun(string name, object globals, int timeout = 5000)
    {
        try
        {
            ScriptRunner<object> runner;

            lock (lockObj)
            {
                if (!cache.TryGetValue(name, out runner))
                    return CompResult.Fail($"脚本未编译: {name}");
            }

            var task = runner(globals);
            if (task.Wait(timeout))
            {
                var result = task.Result;
                return CompResult.Success(result?.ToString());
            }

            return CompResult.Fail($"执行超时 ({timeout}ms)");
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"执行异常: {ex.Message}");
        }
    } 
    #endregion

    #region 辅助方法
    private ScriptOptions CreateOptions()
    {
        Compiler.ClearMetaRefs(); // 清理旧的元数据引用

        var refs = Compiler.GetMetaRefs();

        // 添加额外的程序集引用
        foreach (var Path in ExtraAsse)
        {
            if (File.Exists(Path))
            {
                try
                {
                    var refToAdd = MetadataReference.CreateFromFile(Path);
                    refs.Add(refToAdd);
                }
                catch { }
            }
        }

        // 默认导入
        var imports = new List<string>
        {
            "System",
            "System.Linq",
            "System.Text",
            "System.Threading.Tasks",
            "System.Collections.Generic",
            "Terraria",
            "TShockAPI",
            "Microsoft.Xna.Framework"
        };

        if (global != null && !string.IsNullOrEmpty(global.Namespace))
        {
            imports.Add(global.Namespace);
        }

        return ScriptOptions.Default
            .WithReferences(refs)
            .WithImports(imports)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithAllowUnsafe(true);
    }
    #endregion

    #region 批量预编译
    public CompResult BatchCompile(string scriptDir, List<string> usings, bool Enabled)
    {
        try
        {
            if (!Directory.Exists(scriptDir))
                return CompResult.Fail($"脚本目录不存在: {scriptDir}");

            var files = Directory.GetFiles(scriptDir, "*.csx", SearchOption.AllDirectories);
            int total = files.Length;
            int yes = 0;
            int no = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    var code = File.ReadAllText(filePath, Encoding.UTF8);

                    CompResult result;
                    if (Enabled)
                    {
                        result = PreCompile(fileName, code, usings);
                    }
                    else
                    {
                        // 只是检查语法
                        var script = CSharpScript.Create<object>(code, CreateOptions());
                        var comp = script.GetCompilation();
                        var errors = comp.GetDiagnostics()
                            .Where(d => d.Severity == DiagnosticSeverity.Error)
                            .ToList();

                        result = errors.Count == 0
                            ? CompResult.Success("语法正确")
                            : Compiler.ErrorScript(fileName, errors);
                    }

                    if (result.Ok)
                    {
                        yes++;
                        TShock.Log.ConsoleInfo($"[自动编译] 脚本编译成功: {fileName}");
                    }
                    else
                    {
                        no++;
                        TShock.Log.ConsoleError($"[自动编译] 脚本编译失败: {fileName} - {result.Msg}");
                    }
                }
                catch (Exception ex)
                {
                    no++;
                    TShock.Log.ConsoleError($"[自动编译] 脚本编译异常: {ex.Message}");
                }
            }

            var msg = $"[自动编译] 批量编译完成 总数{total}, 成功{yes}, 失败{no}";
            TShock.Log.ConsoleInfo(msg);
            return CompResult.Success(msg);
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"批量编译异常: {ex.Message}");
        }
    }
    #endregion

    #region 释放方法
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (!isDisposed)
        {
            if (disposing)
            {
                lock (lockObj)
                {
                    // 彻底清空缓存
                    if (cache != null)
                    {
                        foreach (var kvp in cache.ToList())  // 复制列表避免迭代异常
                        {
                            cache[kvp.Key] = null;
                        }
                        cache.Clear();
                    }
                }

                // 清空额外程序集引用
                if (ExtraAsse != null)
                {
                    ExtraAsse.Clear();
                }

                // 强制清理编译器资源
                Compiler.ClearMetaRefs();
            }
            isDisposed = true;
        }
    }

    // 给调用它的插件用的 因为不知道对方什么时候开始编译 先保留方法
    public void ClearCache(string name = null)
    {
        lock (lockObj)
        {
            if (name == null)
                cache.Clear();
            else
                cache.Remove(name);
        }
    }
    #endregion
}