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
    [JsonProperty("包含子目录", Order = 3)]
    public bool IncludeSub = true;  
    [JsonProperty("语言版本", Order = 4)]
    public string LangVer = "CSharp11";   
    [JsonProperty("最大文件数", Order = 5)]
    public int MaxFiles = 100;  
    [JsonProperty("最大大小MB", Order = 6)]
    public int MaxSizeMB = 50;
    [JsonProperty("默认添加引用", Order = 7)]
    public List<string> DefaultUsings = new List<string>();

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
           "语言版本: C#语言版本(CSharp8-CSharp11)",
           "最大文件数: 单次编译最多处理文件数",
           "最大大小MB: 所有.cs文件总大小限制",
           "默认添加引用: 为所有cs文件添加默认引用(已存在则不添加)",
           "注意：暂时不支持内嵌资源的插件生成",
        };
        DefaultUsings = new List<string>()
        {
            "using System;",
            "using System.Collections;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            "using System.Text;",
            "using System.Threading.Tasks;",
            "using System.IO;",
            "using System.IO.Streams;",
            "using System.IO.Compression;",
            "using System.Reflection;",
            "using System.Diagnostics;",
            "using System.Globalization;",
            "using System.Security;",
            "using System.Security.Cryptography;",
            "using System.Net;",
            "using System.Runtime.CompilerServices;",
            "using System.Runtime.InteropServices;",
            "using Microsoft.Xna.Framework;",
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