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
        Compiler.ClearMetaRefs(); // 清理旧的元数据引用

        var refs = Compiler.GetMetaRefs();

        // 默认导入
        var imports = Config.Usings;

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
    #endregion

    #region 批量预编译
    public CompResult BatchCompile(string scriptDir, List<string> usings)
    {
        try
        {
            if (!Directory.Exists(scriptDir))
                return CompResult.Fail($"脚本目录不存在: {scriptDir}");

            var files = Directory.GetFiles(scriptDir, "*.csx", SearchOption.AllDirectories);
            int total = files.Length;
            int yes = 0;
            int no = 0;
            int ce = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    var code = File.ReadAllText(filePath, Encoding.UTF8);

                    CompResult result = PreCompile(fileName, code, usings);
                    if (result.Ok && result.Msg != "已缓存")
                    {
                        yes++;
                        TShock.Log.ConsoleInfo($"[自动编译] {fileName} (编译成功)");
                    }
                    else if (result.Msg == "已缓存")
                    {
                        ce++;
                        TShock.Log.ConsoleInfo($"[自动编译] {fileName} (使用缓存)");
                    }
                    else
                    {
                        no++;
                        TShock.Log.ConsoleError($"[自动编译] {fileName} (编译失败)\n" +
                                                $"{result.Msg}");
                    }
                }
                catch (Exception ex)
                {
                    no++;
                    TShock.Log.ConsoleError($"[自动编译] 编译异常: {ex.Message}");
                }
            }

            // 动态构建消息，只显示存在的数量
            var part = new List<string>{ $"总计'{total}个'脚本" };
            if (yes > 0) part.Add($"编译成功{yes}个");
            if (ce > 0) part.Add($"使用缓存{ce}个");
            if (no > 0) part.Add($"编译失败{no}个");
            var msg = $"\n[自动编译] {string.Join(" ", part)}";
            TShock.Log.ConsoleInfo(msg);
            TShock.Log.ConsoleInfo($"[自动编译] 脚本存放路径 {scriptDir}\n");
            return CompResult.Success(msg);
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"批量编译异常: {ex.Message}");
        }
        finally
        {
            // 编译后立即清理元数据引用，释放内存
            Compiler.ClearMetaRefs();
            GC.Collect(2, GCCollectionMode.Default);
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
                        cache.Clear();
                    }

                    if (hashMap != null)
                    {
                        hashMap.Clear();
                    }
                }

                // 强制清理编译器资源
                Compiler.ClearMetaRefs();
            }
            isDisposed = true;
        }
    }
    #endregion
}