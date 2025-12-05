using Newtonsoft.Json;
using TShockAPI;

namespace AutoCompile;

internal class Configuration
{
    internal static readonly string Paths = Path.Combine(TShock.SavePath, "自动编译");
    public static readonly string FilePath = Path.Combine(Paths, "自动编译.json");
    [JsonProperty("使用说明", Order = -10)]
    public List<string> UsageTips = new List<string>();
    [JsonProperty("启用", Order = 0)]
    public bool Enabled = true;
    [JsonProperty("包含子目录", Order = 1)]
    public bool IncludeSub = true;
    [JsonProperty("智能修复重试次数", Order = 2)]
    public int RetryCount = 3;
    [JsonProperty("编译失败日志显示英文", Order = 3)]
    public bool ShowErrorEnglish { get; set; } = true;
    [JsonProperty("编译失败日志显示中文", Order = 4)]
    public bool ShowErrorChinese { get; set; } = true;
    [JsonProperty("成功后清失败日志文件", Order = 5)]
    public bool ClearLogs = true;
    [JsonProperty("语言版本", Order = 6)]
    public string LangVer = "CSharp11";
    [JsonProperty("最大文件数", Order = 7)]
    public int MaxFiles = 100;
    [JsonProperty("最大大小MB", Order = 8)]
    public int MaxSizeMB = 50;
    [JsonProperty("默认给源码添加引用", Order = 9)]
    public List<string> Usings = new List<string>();
    [JsonProperty("系统程序集", Order = 10)]
    public List<string> SystemAsse = new List<string>();

    #region 预设参数方法
    public void SetDefault()
    {
        UsageTips = new List<string>()
        {
           "1. 将所有.cs文件放入[自动编译]文件夹里的[源码]文件夹",
           "2. 使用命令 /cs by 进行编译",
           "3. 生成的DLL在[自动编译]文件夹里的[编译输出]文件夹",
           "【配置项说明】",
           "启用: 开关插件功能",
           "包含子目录: 是否扫描[源码]子目录中的.cs文件",
           "语言版本: C#语言版本(CSharp8-CSharp14)",
           "最大文件数: 单次编译最多处理文件数",
           "最大大小MB: 所有.cs文件总大小限制",
           "默认添加引用: 为所有cs文件添加默认引用" +
           "(已存在则不添加,只需写命名空间自动添加using)",
            "【智能修复】",
           "当编译出现缺失命名空间错误时，插件会自动:",
           "1. 分析错误信息，提取缺失的命名空间",
           "2. 从源代码中移除相关的using语句",
           "3. 重新尝试编译",
           "4. 根据配置的重试次数重复此过程",
           "注意：暂时不支持内嵌资源的插件生成",
        };

        Usings = new List<string>()
        {
            "System",
            "System.Collections",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Threading",
            "System.Threading.Tasks",
            "System.IO",
            "System.IO.Streams",
            "System.IO.Compression",
            "System.Reflection",
            "System.Diagnostics",
            "System.Globalization",
            "System.Security",
            "System.Security.Cryptography",
            "System.Net",
            "System.Net.Http",
            "System.Runtime.CompilerServices",
            "System.Runtime.InteropServices",
            "Microsoft.Xna.Framework",
        };

        SystemAsse = new List<string>() 
        { 
            // 系统核心相关
            "System.dll",
            "System.Web.dll",
            "System.Web.HttpUtility.dll",
            "System.Net.dll",
            "System.Net.Http.dll",
            "System.Net.Requests.dll",
            "System.Net.Primitives.dll",
            "System.Private.CoreLib.dll",
            "System.Private.Uri.dll",
            "System.Runtime.dll",
            "netstandard.dll",
            "System.Core.dll",
            "System.Private.Xml.Linq.dll",
            "System.Diagnostics.TraceSource.dll",
            
            // 集合相关
            "System.Collections.dll",
            "System.Collections.Concurrent.dll",
            "System.Collections.Immutable.dll",

            // LinQ相关
            "System.Linq.dll",
            "System.Linq.Expressions.dll",
            "System.Linq.Queryable.dll",

            // IO
            "System.IO.dll","System.IO.FileSystem.dll","System.IO.FileSystem.Primitives.dll",

            // GZip
            "System.IO.Compression.dll","System.IO.Compression.ZipFile.dll",

            // 文本处理
            "System.Text.Json.dll",
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
            "System.Diagnostics.Process.dll",
            "System.AppContext.dll",
            "System.Console.dll",
            "System.Security.Cryptography.Algorithms.dll",
            "System.Security.Cryptography.Primitives.dll",
            "System.Security.Principal.dll",
        };
    }
    #endregion

    #region 读取与创建配置文件方法
    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(FilePath, json);
    }

    public static Configuration Read()
    {
        if (!File.Exists(FilePath))
        {
            var NewConfig = new Configuration();
            NewConfig.SetDefault();
            new Configuration().Write();
            return NewConfig;
        }
        else
        {
            string jsonContent = File.ReadAllText(FilePath);
            return JsonConvert.DeserializeObject<Configuration>(jsonContent)!;
        }
    }
    #endregion
}