using Terraria;
using TShockAPI;
using TShockAPI.Hooks;
using TerrariaApi.Server;
using System.Reflection;
using System.Text;

namespace AutoCompile;

[ApiVersion(2, 1)]
public class AutoCompile : TerrariaPlugin
{
    #region 插件信息
    public override string Name => "自动编译插件";
    public override string Author => "羽学";
    public override Version Version => new(1, 0, 6);
    public override string Description => "使用cs指令自动编译CS源码为DLL插件,支持其他插件引用本插件实现C#脚本编译执行";
    #endregion

    #region 注册卸载事件
    public AutoCompile(Main game) : base(game) { }
    public override void Initialize()
    {
        // 释放内嵌资源
        ExtractData();
        LoadCfg();
        GeneralHooks.ReloadEvent += ReloadCfg;
        TShockAPI.Commands.ChatCommands.Add(new Command("compile.use", Cmd.MainCmd, "cs"));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GeneralHooks.ReloadEvent -= ReloadCfg;
            TShockAPI.Commands.ChatCommands.RemoveAll(x => x.CommandDelegate == Cmd.MainCmd);
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 内嵌依赖项管理
    private void ExtractData()
    {
        if (!Directory.Exists(Configuration.Paths))
            Directory.CreateDirectory(Configuration.Paths);

        var CodePath = Path.Combine(Configuration.Paths, "源码");
        if (!Directory.Exists(CodePath))
            Directory.CreateDirectory(CodePath);

        var OutPath = Path.Combine(Configuration.Paths, "编译输出");
        if (!Directory.Exists(OutPath))
            Directory.CreateDirectory(OutPath);

        var AsmPath = Path.Combine(Configuration.Paths, "程序集");
        if (!Directory.Exists(AsmPath))
            Directory.CreateDirectory(AsmPath);

        Copy();
        Copy2(AsmPath);
    }
    #endregion

    #region 释放本插件依赖项与编译DLL所需程序集
    private static void Copy()
    {
        var PluginPath = Path.Combine(typeof(TShock).Assembly.Location, "ServerPlugins");
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetName().Name!;
        foreach (var res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith($"{name}.依赖项."))
                continue;

            string fileName = res.Substring(name.Length + "依赖项.".Length + 1);

            var tarPath = Path.Combine(PluginPath, fileName);

            if (File.Exists(tarPath)) continue;

            using (var stream = asm.GetManifestResourceStream(res))
            {
                if (stream == null) continue;

                using (var fs = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                {
                    stream.CopyTo(fs);
                }
            }
        }
    }

    private void Copy2(string path)
    {
        var asm = Assembly.GetExecutingAssembly();
        string name = asm.GetName().Name!;
        foreach (string res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith($"{name}.程序集."))
                continue;

            string fileName = res.Substring(name.Length + "程序集.".Length + 1);
            string tarPath = Path.Combine(path, fileName);

            if (File.Exists(tarPath)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);

            using (var stream = asm.GetManifestResourceStream(res))
            {
                if (stream == null) continue;

                using (var fs = new FileStream(tarPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                {
                    stream.CopyTo(fs);
                }
            }
        }
    }
    #endregion

    #region 配置处理
    internal static Configuration Config = new();
    private static void ReloadCfg(ReloadEventArgs args)
    {
        LoadCfg();
        args.Player?.SendSuccessMessage("[自动编译] 重载配置完成");
    }
    private static void LoadCfg()
    {
        Config = Configuration.Read();
        Config.Write();
    }
    #endregion
}