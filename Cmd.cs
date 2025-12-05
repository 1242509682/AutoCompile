using System.Text;
using TShockAPI;
using static AutoCompile.Utils;

namespace AutoCompile;

internal class Cmd
{
    #region 主命令
    internal static void MainCmd(CommandArgs args)
    {
        if (!AutoCompile.Config.Enabled)
        {
            args.Player.SendErrorMessage("自动编译插件已关闭");
            return;
        }

        var plr = args.Player;

        if (args.Parameters.Count == 0)
        {
            ShowHelp(plr);
            return;
        }

        var cmd = args.Parameters[0].ToLower();

        switch (cmd)
        {
            case "by":
            case "编译":
            case "build":
                CompileCmd(plr, args.Parameters);
                break;

            case "lj":
            case "路径":
            case "path":
                ShowPath(plr);
                break;

            case "ql":
            case "清理":
            case "clear":
                Utils.CleanCodeFiles();
                plr.SendSuccessMessage("已清理'源码'文件夹");
                break;

            case "开":
            case "on":
                EnableCmd(plr, true);
                break;

            case "关":
            case "off":
                EnableCmd(plr, false);
                break;

            case "pz":
            case "cfg":
            case "config":
            case "配置":
                ShowCfg(plr);
                break;

        }
    }
    #endregion

    #region 编译命令
    private static void CompileCmd(TSPlayer plr, List<string> parameters)
    {
        try
        {
            plr.SendInfoMessage("开始编译...");
            var startMem = LogsMag.GetMemInfo();
            plr.SendInfoMessage($"- 编译前 {startMem}");

            var result = Compiler.CompAll();

            if (result.Ok)
            {
                // 显示完成时的内存信息
                var endMem = LogsMag.GetMemInfo();
                plr.SendInfoMessage($"- 编译后 {endMem}");
                plr.SendSuccessMessage(result.Msg);
                ShowFiles(plr);
            }
            else
            {
                plr.SendErrorMessage(result.Msg);

                if (result.Msg.Contains("编译错误"))
                {
                    plr.SendInfoMessage("提示: 检查代码语法和命名空间");
                    plr.SendInfoMessage("提示: 生成PDB文件可查看详细错误位置");
                }
            }
        }
        catch (Exception ex)
        {
            plr.SendErrorMessage($"编译命令异常: {ex.Message}");
            TShock.Log.ConsoleError($"【自动编译】 命令异常: {ex}");
        }
    }
    #endregion

    #region 开关命令
    private static void EnableCmd(TSPlayer plr, bool enabled)
    {
        AutoCompile.Config.Enabled = enabled;
        AutoCompile.Config.Write();
        plr.SendSuccessMessage($"已{(enabled ? "开启" : "关闭")}插件");
    }
    #endregion

    #region 配置命令
    private static void ShowCfg(TSPlayer plr)
    {
        var CodePath = Path.Combine(Configuration.Paths, "源码");
        var AsmPath = Path.Combine(Configuration.Paths, "程序集");
        var OutPath = Path.Combine(Configuration.Paths, "编译输出");

        try
        {
            var cfg = AutoCompile.Config;
            var msg = new StringBuilder();

            msg.AppendLine("当前配置:");
            msg.AppendLine($"  启用: {(cfg.Enabled ? "是" : "否")}");
            msg.AppendLine($"  语言版本: {cfg.LangVer}");
            msg.AppendLine($"  源码路径: {CodePath}");
            msg.AppendLine($"  引用路径: {AsmPath}");
            msg.AppendLine($"  输出路径: {OutPath}");
            msg.AppendLine($"  包含子目录: {(cfg.IncludeSub ? "是" : "否")}");
            msg.AppendLine($"  最大文件: {cfg.MaxFiles}");
            msg.AppendLine($"  最大大小: {cfg.MaxSizeMB}MB");
            plr.SendMessage(msg.ToString(), color2);
        }
        catch (Exception ex)
        {
            plr.SendErrorMessage($"显示配置失败: {ex.Message}");
        }
    }
    #endregion

    #region 显示文件
    private static void ShowFiles(TSPlayer plr)
    {
        try
        {
            var dllFiles = GetDllFiles();

            if (dllFiles.Count == 0)
            {
                plr.SendInfoMessage("暂无编译文件");
                return;
            }

            var outDir = Path.Combine(Configuration.Paths, "编译输出");
            var msg = new StringBuilder("最新编译文件:");

            foreach (var fileName in dllFiles.Take(5))
            {
                var filePath = Path.Combine(outDir, fileName);
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    var sizeKB = fileInfo.Length / 1024.0;
                    var time = fileInfo.LastWriteTime.ToString("HH:mm:ss");
                    msg.AppendLine($"{fileName} ({sizeKB:F1}KB, {time})");
                }
                else
                {
                    msg.AppendLine($"  {fileName}");
                }
            }

            if (dllFiles.Count > 5)
                msg.AppendLine($"  ... 共{dllFiles.Count}个文件");

            GradMess(plr, msg);
        }
        catch (Exception ex)
        {
            plr.SendErrorMessage($"获取文件列表失败: {ex.Message}");
        }
    }
    #endregion

    #region 显示路径
    public static void ShowPath(TSPlayer plr)
    {
        try
        {
            var srcPath = Path.Combine(Configuration.Paths, "源码");
            var outPath = Path.Combine(Configuration.Paths, "编译输出");
            var binPath = Path.Combine(typeof(TShock).Assembly.Location, "bin");

            var msg = new StringBuilder();
            msg.AppendLine($"源码路径: {srcPath}");
            msg.AppendLine($"输出路径: {outPath}");
            msg.AppendLine($"依赖目录: {binPath}");

            if (Directory.Exists(srcPath))
            {
                var files = Directory.GetFiles(srcPath, "*.cs", SearchOption.AllDirectories);
                msg.AppendLine($"找到 {files.Length} 个.cs文件");
            }

            if (Directory.Exists(outPath))
            {
                var dllFiles = Directory.GetFiles(outPath, "*.dll");
                var pdbFiles = Directory.GetFiles(outPath, "*.pdb");
                msg.AppendLine($"已有 {dllFiles.Length} 个DLL, {pdbFiles.Length} 个PDB");
            }

            plr.SendMessage(msg.ToString(), Utils.color1);
        }
        catch (Exception ex)
        {
            plr.SendErrorMessage($"获取路径失败: {ex.Message}");
        }
    }
    #endregion

    #region 获取需要删除的编译输出文件
    public static List<string> GetDllFiles()
    {
        try
        {
            var outDir = Path.Combine(Configuration.Paths, "编译输出");
            if (!Directory.Exists(outDir))
                return new List<string>();

            return Directory.GetFiles(outDir, "*.dll")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 获取DLL文件列表失败: {ex.Message}");
            return new List<string>();
        }
    }
    #endregion

    #region 帮助命令
    private static void ShowHelp(TSPlayer plr)
    {
        if (plr.RealPlayer)
        {
            plr.SendMessage("[i:509][c/AD89D5:自][c/D68ACA:动][c/DF909A:编][c/E5A894:译] " +
                "[C/BFDFEA:by] [c/00FFFF:羽学]", color1);

            var msg = new StringBuilder();
            msg.AppendLine($"/cs 编译(by) ——编译源码为DLL");
            msg.AppendLine($"/cs 清理(ql) ——清理源码文件夹");
            msg.AppendLine($"/cs 路径(lj) ——显示路径信息");
            msg.AppendLine($"/cs 配置(pz) ——查看配置");
            msg.AppendLine($"/cs 开/关 ——开关插件");

            GradMess(plr, msg);
        }
        else
        {
            plr.SendMessage($"《自动编译插件》\n" +
                $"/cs 编译(by) - 编译源码为DLL\n" +
                $"/cs 清理(ql) - 清理源码文件夹\n" +
                $"/cs 路径(lj) - 显示路径\n" +
                $"/cs 配置(pz) - 查看配置", color1);
        }
    }
    #endregion
}