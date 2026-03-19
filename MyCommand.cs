using System.Text;
using TShockAPI;
using static AutoCompile.Utils;

namespace AutoCompile;

internal class MyCommand
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

            case "fby":
            case "decompile":
            case "反编译":
                DecompileCmd(plr, args.Parameters);
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

    #region 反编译指令方法
    private static void DecompileCmd(TSPlayer plr, List<string> part)
    {
        if (part.Count < 2)
        {
            // 显示所有可用的 DLL 文件列表（带索引）
            var dllLists = GetAllDllFiles();
            if (dllLists.Count == 0)
            {
                plr.SendErrorMessage("没有找到任何可反编译的 DLL 文件。");
                return;
            }

            var msg = new StringBuilder();
            msg.AppendLine("可反编译的 DLL 文件列表：");
            for (int i = 0; i < dllLists.Count; i++)
            {
                string fileName = Path.GetFileName(dllLists[i]);
                msg.AppendLine($"[{i + 1}] {fileName}");
            }

            plr.SendMessage(msg.ToString(), color2);
            plr.SendMessage("/cs fby ——列出所有插件", color1);
            plr.SendMessage("/cs fby <文件索引> ——生成单文件", color1);
            plr.SendMessage("/cs fby <文件索引> -s ——按类型拆分(推荐)", color1);
            plr.SendMessage("/cs fby <文件索引> 命名空间.类名 ——仅反编译指定类型", color1);
            plr.SendInfoMessage("注意:");
            plr.SendInfoMessage("1.通过自动读取《ServerPlugins》与《编译输出》获取文件");
            plr.SendInfoMessage("2.文件索引为插件名字前面的序号");
            plr.SendInfoMessage("3.支持导出内嵌资源文件,资源命名可能不对,但资源是完整能用的");
            return;
        }

        // 检查第二个参数是否是数字（索引）
        bool isIndex = int.TryParse(part[1], out int index);
        string dllName;
        string dllPath;

        // 按索引选择
        var dllList = GetAllDllFiles();
        if (index < 1 || index > dllList.Count)
        {
            plr.SendErrorMessage($"文件索引 {index} 无效，有效范围 1-{dllList.Count}。");
            return;
        }
        dllPath = dllList[index - 1];
        dllName = Path.GetFileName(dllPath); // 仅用于显示}

        // 处理 -s 标志和类型名称
        bool split = part.Contains("-s");
        if (split) part.Remove("-s");

        try
        {
            if (split)
            {
                string srcDir = Path.Combine(Configuration.Paths, "源码");
                string outSubDir = Path.Combine(srcDir, Path.GetFileNameWithoutExtension(dllName) + "_源码");
                int count = (int)DeCompilers.Decompile(dllPath, DecompMode.Split, outDir: outSubDir);
                plr.SendMessage($"反编译完成，共生成 {count} 个cs文件，保存至: \n{outSubDir}",color2);
            }
            else if (part.Count >= 3 && !part[2].StartsWith("-"))
            {
                string typeName = string.Join(" ", part.Skip(2));
                string code = (string)DeCompilers.Decompile(dllPath, DecompMode.One, typeName: typeName);
                string srcDir = Path.Combine(Configuration.Paths, "源码");
                string safeTypeName = CleanName(typeName.Replace('.', '_'));
                string outFile = Path.Combine(srcDir, $"{Path.GetFileNameWithoutExtension(dllName)}_{safeTypeName}.cs");
                File.WriteAllText(outFile, code, Encoding.UTF8);
                plr.SendMessage($"类型反编译完成，已保存到: \n{outFile}", color2);
            }
            else
            {
                string code = (string)DeCompilers.Decompile(dllPath, DecompMode.All);
                string srcDir = Path.Combine(Configuration.Paths, "源码");
                string outFile = Path.Combine(srcDir, Path.GetFileNameWithoutExtension(dllName) + ".cs");
                File.WriteAllText(outFile, code, Encoding.UTF8);
                plr.SendMessage($"反编译完成，已保存到: \n{outFile}", color2);
            }
        }
        catch (Exception ex)
        {
            plr.SendErrorMessage($"反编译失败: {ex.Message}");
            TShock.Log.ConsoleError($"【自动编译】 反编译异常: {ex}");
        }
    }
    #endregion

    #region 获取所有可反编译的 DLL 文件列表
    private static List<string> GetAllDllFiles()
    {
        var files = new List<string>();
        var searchDirs = new List<string>
        {
           Path.Combine(Configuration.Paths, "编译输出"),
           Path.Combine(typeof(TShock).Assembly.Location, "ServerPlugins"),
           Path.GetDirectoryName(typeof(TShock).Assembly.Location)!
        };

        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var dlls = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
                files.AddRange(dlls);
            }
            catch { }
        }

        // 去重
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 应用排除列表（忽略大小写，只比较文件名）
        var exclude = AutoCompile.Config.DeCompileExclude ?? new List<string>();
        if (exclude.Count > 0)
        {
            files = files.Where(f => !exclude.Any(e => string.Equals(Path.GetFileName(f), e, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        return files;
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
            msg.AppendLine($"/cs 反编译(fby) ——把DLL转换为cs源码");
            msg.AppendLine($"/cs 清理(ql) ——清理源码文件夹");
            msg.AppendLine($"/cs 路径(lj) ——显示路径信息");
            msg.AppendLine($"/cs 配置(pz) ——查看配置");
            msg.AppendLine($"/cs 开/关 ——开关插件");
            msg.AppendLine(); // 空行分隔
            msg.AppendLine("编译时需将.cs文件放入【自动编译/源码】文件夹");
            msg.AppendLine("反编译时需将DLL放入【自动编译/编译输出】文件夹（或指定完整路径）");
            msg.AppendLine("没有找到文件名,自动扫描ServerPlugins文件夹");

            GradMess(plr, msg);
        }
        else
        {
            plr.SendMessage($"《自动编译插件》\n" +
                $"/cs 编译(by) - 编译源码为DLL\n" +
                $"/cs 反编译(fby) - 把DLL转换为cs源码\n" +
                $"/cs 清理(ql) - 清理源码文件夹\n" +
                $"/cs 路径(lj) - 显示路径\n" +
                $"/cs 配置(pz) - 查看配置\n" +
                $"\n编译时需将.cs文件放入【自动编译/源码】文件夹\n" +
                $"反编译时需将DLL放入【自动编译/编译输出】文件夹（或指定完整路径）\n" +
                "如没有找到文件名,自动扫描ServerPlugins文件夹", color1);
        }
    }
    #endregion
}