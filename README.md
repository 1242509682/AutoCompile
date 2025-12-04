# AutoCompile 自动编译插件

- 作者: 羽学
- 出处: 无
- 这是一个Tshock服务器插件，主要用于：将CSharp代码放入【自动编译】=>【源码】文件夹内使用/cs by指令将源码编译成可允许的Dll插件,可将依赖项放入到【程序集】文件夹自动引用。

## 更新日志

```
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
| /cs lj | /cs 路径 |   compile.use    |    显示插件的所有路径    |
| /cs ql | /cs 清理 |   compile.use    |    清理编译输出文件夹    |
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
    "语言版本: C#语言版本(CSharp8-CSharp11)",
    "最大文件数: 单次编译最多处理文件数",
    "最大大小MB: 所有.cs文件总大小限制",
    "默认添加引用: 为所有cs文件添加默认引用(已存在则不添加)",
    "注意：暂时不支持内嵌资源的插件生成"
  ],
  "启用": true,
  "包含子目录": true,
  "语言版本": "CSharp11",
  "最大文件数": 100,
  "最大大小MB": 50,
  "默认添加引用": [
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
    "using Microsoft.Xna.Framework;"
  ]
}
```
## 反馈
- 优先发issued -> 共同维护的插件库：https://github.com/UnrealMultiple/TShockPlugin
- 次优先：TShock官方群：816771079
- 大概率看不到但是也可以：国内社区trhub.cn ，bbstr.net , tr.monika.love