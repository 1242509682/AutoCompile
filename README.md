# AutoCompile 自动编译插件

- 作者: 羽学
- 出处: 无
- 这是一个Tshock服务器插件，主要用于：将CSharp代码放入【自动编译】=>【源码】文件夹内使用/cs by指令将源码编译成可允许的Dll插件,可将依赖项放入到【程序集】文件夹自动引用。

## 更新日志

```
v1.0.4
加入了动态编译C#脚本功能与执行器
此功能仅用于与其他插件对接时使用
优化了编译DLL的内存占用与释放
（之前每次编译DLL都会有10-20m的内存增长）

v1.0.3
移除了【智能修复】避免误伤某些命名空间导致编译失败
恢复了【移除指定using语句】配置项(比较稳妥,自己决定是否移除)

v1.0.2
优化了每次编译后可能的内存泄露
将/cs ql 改为清理“源码”文件夹
加入了编译前后的内存对比显示
移除了分组编译逻辑，避免误判报错，只允许放入同命名空间的单插件源码进行编译
移除了【移除指定Using】配置项
重构编译构建逻辑，加入了智能修复(检测缺失命名空间自动移除,尝试重新编译)

v1.0.1
加入了少量的报错汉化与报错日志文件输出
加入了修复中文编码（针对于某些喜欢在switch类型添加中文指令插件)
支持指定引用系统自带的“运行时”程序集
修复了报错后不会强制GC回收内存的BUG
内嵌了更多程序集，经过上百款主流插件源码测试，除了构架太老的源码基本上都能编译通过（说的是本身源码就没错误的那些）
注意：本插件可以编译CS8-14的代码，但CS11以上无法在TShock5运行请升级TShock6
特别注意：我知道你懒，但也别把bin文件夹和obj文件夹丢进去省得报错了还怪插件不行！

v1.0.0
利用Roslyn编译器实现功能，为解决没有电脑的手机服主提供便利
暂时不支持内嵌文件的插件源码(没做相关适配,但插件生成出来是可用的,就是没内嵌文件)
需要统一所有CS源码命名空间
自动补充引用TShock和OTAPI的DLL
额外引用程序集放入到【程序集】文件夹
编译后的DLL自动放入到【编译输出】文件夹
主要指令只有一个，其他的没什么用：/cs by
```

## 指令

| 语法                             | 别名  |       权限       |                   说明                   |
| -------------------------------- | :---: | :--------------: | :--------------------------------------: |
| /cs  | 无 |   compile.use    |    菜单指令    |
| /cs on或者off | compile.use |   compile.use    |    开启或关闭插件功能    |
| /cs by | /cs 编译 |   compile.use    |    编译所有CS源码为DLL    |
| /cs ql | /cs 清理 |   compile.use    |    清理“源码”文件夹    |
| /cs lj | /cs 路径 |   compile.use    |    显示插件的所有路径    |
| /cs pz | /cs 配置 |   compile.use    |    显示当前配置项    |
| /reload  | 无 |   tshock.cfg.reload    |    重载配置文件    |

## 配置
> 配置文件位置：tshock/模版.json
```json
{
  "使用说明": [
    "1. 将所有.cs文件放入[自动编译]文件夹里的[源码]文件夹",
    "2. 使用命令 /cs by 进行编译",
    "3. 生成的DLL在[自动编译]文件夹里的[编译输出]文件夹",
    "【配置项说明】",
    "启用: 开关插件功能",
    "包含子目录: 是否扫描[源码]子目录中的.cs文件",
    "语言版本: C#语言版本(CSharp8-CSharp14)",
    "最大文件数: 单次编译最多处理文件数",
    "最大大小MB: 所有.cs文件总大小限制",
    "默认添加引用: 为所有cs文件添加默认引用(已存在则不添加,只需写命名空间自动添加using)",
    "移除using语句:自动移除已存在的using语句",
    "注意：暂时不支持内嵌资源的插件生成"
  ],
  "启用": true,
  "包含子目录": true,
  "编译失败日志显示英文": true,
  "编译失败日志显示中文": true,
  "成功后清失败日志文件": true,
  "语言版本": "CSharp11",
  "最大文件数": 100,
  "最大大小MB": 50,
  "移除指定using语句": [
    "Steamworks",
    "System.Numerics",
    "System.Security.Policy",
    "Org.BouncyCastle.Asn1.Cmp",
    "Org.BouncyCastle.Asn1.X509",
    "NuGet.Protocol.Plugins",
    "Org.BouncyCastle.Math.EC.ECCurve",
    "using static Org.BouncyCastle.Math.EC.ECCurve;",
    "using static MonoMod.InlineRT.MonoModRule;"
  ],
  "默认给源码添加引用": [
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
    "Microsoft.Xna.Framework"
  ],
  "系统程序集": [
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
    "System.Collections.dll",
    "System.Collections.Concurrent.dll",
    "System.Collections.Immutable.dll",
    "System.Linq.dll",
    "System.Linq.Expressions.dll",
    "System.Linq.Queryable.dll",
    "System.IO.dll",
    "System.IO.FileSystem.dll",
    "System.IO.FileSystem.Primitives.dll",
    "System.IO.Compression.dll",
    "System.IO.Compression.ZipFile.dll",
    "System.Text.Json.dll",
    "System.Text.RegularExpressions.dll",
    "System.Text.Encoding.dll",
    "System.Text.Encoding.Extensions.dll",
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
    "System.Security.Principal.dll"
  ]
}
```
## 反馈
- 优先发issued -> 共同维护的插件库：https://github.com/UnrealMultiple/TShockPlugin
- 次优先：TShock官方群：816771079
- 大概率看不到但是也可以：国内社区trhub.cn ，bbstr.net , tr.monika.love