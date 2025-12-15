using System.Collections.Concurrent;
using System.Reflection;
using TShockAPI;
using static AutoCompile.AutoCompile;

namespace AutoCompile;

#region 脚本服务接口类（简化版）
public static class ScriptMag
{
    private static readonly object lockObj = new();
    private static readonly ConcurrentDictionary<string, ScriptExec> execs = new();
    private static readonly ConcurrentDictionary<string, PluginReg> RegInfo = new();
    private static bool isDisposed = false;

    #region 插件注册信息类
    private sealed class PluginReg
    {
        public string Key { get; set; } = "";
        public bool AutoComp { get; set; }
        public string Dir { get; set; } = "";
        public Type Gtype { get; set; }
        public HashSet<string> Usings { get; set; } = new();
    }
    #endregion

    #region 生成唯一key
    private static string MakeKey(Assembly asm)
    {
        return $"{asm.GetName().FullName}";
    }
    #endregion

    #region 注册脚本服务
    public static ScriptExec Register(Assembly asm, string dir, Type gtype, bool autoComp, List<string> usings)
    {
        lock (lockObj)
        {
            if (isDisposed) return null;

            // 执行器已存在则返回
            var key = MakeKey(asm);
            if (execs.TryGetValue(key, out var exec)) return exec;

            try
            {
                exec = new ScriptExec(gtype);
                execs[key] = exec;

                // 创建注册信息
                var reg = new PluginReg
                {
                    AutoComp = autoComp,
                    Dir = dir,
                    Gtype = gtype,
                    Key = key
                };

                // 合并Usings
                if (usings != null)
                {
                    foreach (var u in usings)
                        reg.Usings.Add(u);
                }

                if (Config.Usings != null)
                {
                    foreach (var u in Config.Usings)
                        reg.Usings.Add(u);
                }

                RegInfo[key] = reg;

                if (autoComp)
                {
                    // 批量编译
                    var result = exec.BatchCompile(dir, reg.Usings.ToList());
                    if (!result.Ok)
                    {
                        TShock.Log.ConsoleError($"[自动编译] 编译失败: {result.Msg}");
                        Remove(key);
                        return null;
                    }
                }
                else
                {
                    TShock.Log.ConsoleError($"[自动编译] 未开启【启动服务器自动编译】,请手动执行编译: /reload ");
                }

                return exec;
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"[自动编译] 注册失败: {ex.Message}");
                Remove(key);
                return null;
            }
        }
    }
    #endregion

    #region 获取执行器
    public static ScriptExec GetExec(Assembly asm)
    {
        var key = MakeKey(asm);
        execs.TryGetValue(key, out var exec);
        return exec;
    }
    #endregion

    #region 重编译
    public static CompResult Reload(Assembly asm)
    {
        var key = MakeKey(asm);

        if (!execs.TryGetValue(key, out var exec) ||
            !RegInfo.TryGetValue(key, out var reg))
            return CompResult.Fail("未注册");

        return exec.BatchCompile(reg.Dir, reg.Usings.ToList());
    }
    #endregion

    #region 帮助复制程序集
    public static bool CopyDll(Assembly asm)
    {
        try
        {
            var toDir = Path.Combine(Configuration.Paths, "程序集");

            // 1. 直接使用程序集的位置
            string srcPath = asm.Location;

            // 2. 如果Location为空或文件不存在，返回false
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
            {
                TShock.Log.ConsoleWarn($"[自动编译] 无法找到程序集文件: {asm.FullName}");
                return false;
            }

            // 3. 使用固定文件名：程序集名称 + .dll
            var fileName = asm.GetName().Name + ".dll";
            var dstFile = Path.Combine(toDir, fileName);

            // 4. 检查是否需要复制（文件不存在或大小不同）
            if (!File.Exists(dstFile) || new FileInfo(srcPath).Length != new FileInfo(dstFile).Length)
            {
                File.Copy(srcPath, dstFile, true);
                TShock.Log.ConsoleInfo($"[自动编译] 已复制: {fileName}");
                return true;
            }

            return false; // 文件已存在且相同
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[自动编译] 复制失败: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region 清理单个插件
    private static void Remove(string key)
    {
        if (execs.TryRemove(key, out var exec))
            exec.Dispose();

        RegInfo.TryRemove(key, out _);
    }

    public static void ClearPlugin(Assembly asm)
    {
        var key = MakeKey(asm);
        Remove(key);
    }
    #endregion

    #region 清理所有
    public static void ClearAll()
    {
        lock (lockObj)
        {
            isDisposed = true;

            foreach (var exec in execs.Values)
                exec.Dispose();

            execs.Clear();
            RegInfo.Clear();
        }
    }
    #endregion
}
#endregion