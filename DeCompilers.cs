using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using TShockAPI;

namespace AutoCompile;

// 反编译模式枚举：用于指定 Decompile 方法的行为
public enum DecompMode
{
    All,    // 单一文件模式：反编译整个程序集，生成一个 .cs 文件
    Split,  // 拆分模式：按每个类型生成单独的 .cs 文件，并按命名空间组织文件夹
    One     // 指定类型模式：仅反编译指定的一个类型，生成一个 .cs 文件
}

public static class DeCompilers
{
    #region 统一反编译入口
    /// <summary>
    /// 统一反编译入口
    /// </summary>
    /// <param name="dllPath">DLL 路径</param>
    /// <param name="mode">模式</param>
    /// <param name="typeName">当 mode=One 时，指定类型全名</param>
    /// <param name="outDir">当 mode=Split 时，指定输出目录；为 null 时自动生成</param>
    /// <returns>All/One 返回 string 代码，Split 返回 int 文件数</returns>
    public static object Decompile(string dllPath, DecompMode mode, string? typeName = null, string? outDir = null)
    {
        // 检查 DLL 文件是否存在，不存在则抛出异常
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("找不到 DLL", dllPath);

        // 创建临时文件副本，避免锁定原始文件
        string tempFile = Path.GetTempFileName();

        try
        {
            // 复制原始 DLL 到临时文件（覆盖）
            File.Copy(dllPath, tempFile, true);

            // 自动提取 DLL 中的内嵌资源（如图片、文本、子 DLL 等）到“源码/[DLL名]_资源”文件夹
            ExtractRes(dllPath);

            // 创建反编译器实例（基于临时文件）
            var dec = CreateDecomp(tempFile, dllPath);

            // 根据模式执行不同的反编译逻辑
            switch (mode)
            {
                // 单文件
                case DecompMode.All:
                    {
                        // 获取原始完整语法树（用于提取 using）
                        var rawTree = dec.DecompileWholeModuleAsSingleFile();

                        // 提取所有 using 声明（包括别名和 using static）
                        var usings = rawTree.Descendants.OfType<UsingDeclaration>().ToList();

                        // 获取所有类型，过滤编译器生成的内置类型
                        var types = dec.TypeSystem.MainModule.TypeDefinitions
                            .Where(t => !t.Name.StartsWith("<"))
                            .Where(t => !t.Name.StartsWith("__"))
                            .ToList();

                        // 获取排除命名空间列表（前缀匹配，忽略大小写）
                        var exclude = AutoCompile.Config.DeCompileExcludeNamespaces ?? new List<string>();
                        if (exclude.Any())
                        {
                            types = types.Where(t =>
                                !exclude.Any(ns =>
                                    !string.IsNullOrEmpty(t.Namespace) &&
                                    t.Namespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase)
                                )
                            ).ToList();
                        }

                        // 排序：优先输出含 ApiVersion 特性的类型，再按命名空间、名称排序
                        var sorted = types
                            .Select(t => new
                            {
                                Type = t,
                                HasApi = t.GetAttributes().Any(a => a.AttributeType.Name.Contains("ApiVersion"))
                            })
                            .OrderByDescending(x => x.HasApi)
                            .ThenBy(x => x.Type.Namespace)
                            .ThenBy(x => x.Type.Name)
                            .Select(x => x.Type)
                            .ToList();

                        // 构建新语法树
                        var newTree = new SyntaxTree();

                        // 将原始 using 添加到新树顶部
                        foreach (var u in usings)
                        {
                            var clone = u.Clone() as UsingDeclaration;
                            if (clone != null)
                                newTree.InsertChildAfter(null, clone, SyntaxTree.MemberRole);
                        }

                        string? curNs = null;
                        AstNode cont = newTree;  // 当前容器（命名空间或根）

                        foreach (var t in sorted)
                        {
                            // 反编译单个类型
                            var tTree = dec.DecompileType(t.FullTypeName);
                            var decl = tTree.Descendants.OfType<EntityDeclaration>().FirstOrDefault();
                            if (decl == null) continue;

                            var clone = decl.Clone() as EntityDeclaration;
                            if (clone == null) continue;

                            string ns = t.Namespace ?? "";
                            if (ns != curNs)
                            {
                                if (!string.IsNullOrEmpty(ns))
                                {
                                    cont = new NamespaceDeclaration(ns);
                                    newTree.AddChild(cont, SyntaxTree.MemberRole);
                                }
                                else
                                {
                                    cont = newTree;
                                }
                                curNs = ns;
                            }
                            cont.AddChild(clone, SyntaxTree.MemberRole);
                        }

                        // 清理特性并返回代码
                        return CleanAttrs(newTree.ToString());
                    }

                // 拆分模式
                case DecompMode.Split:
                    {
                        // 如果未指定输出目录，则自动生成一个文件夹：源码/[DLL名]_源码
                        string finalOutDir = outDir ?? Path.Combine(Configuration.Paths, "源码",
                            Path.GetFileNameWithoutExtension(dllPath) + "_源码");

                        // 获取程序集中所有类型定义，并过滤掉编译器生成的辅助类型（名称以 "<" 开头）
                        var types = dec.TypeSystem.MainModule.TypeDefinitions
                            .Where(t => !t.Name.StartsWith("<"))
                            .Where(t => !t.Name.StartsWith("__"))  // 新增：排除以双下划线开头的类型（如 __StaticArrayInitTypeSize=28）
                            .ToList();

                        // 获取排除命名空间列表（用来排除例如:Microsoft.CodeAnalysis和System.Runtime.CompilerServices）
                        var excludeNs = AutoCompile.Config.DeCompileExcludeNamespaces ?? new List<string>();
                        if (excludeNs.Any())
                        {
                            // 前缀匹配（忽略大小写）
                            types = types.Where(t =>
                                !excludeNs.Any(ns =>
                                    !string.IsNullOrEmpty(t.Namespace) &&
                                    t.Namespace.StartsWith(ns, StringComparison.OrdinalIgnoreCase)
                                )
                            ).ToList();
                        }

                        // 记录生成的文件数量
                        int cnt = 0;
                        foreach (var t in types)
                        {
                            // 反编译当前类型，得到代码
                            string code = CleanAttrs(dec.DecompileType(t.FullTypeName).ToString());
                            // 根据类型的命名空间生成子文件夹路径（将命名空间中的 '.' 替换为路径分隔符）
                            string nsPath = t.Namespace.Replace('.', Path.DirectorySeparatorChar);
                            string dir = Path.Combine(finalOutDir, nsPath);
                            Directory.CreateDirectory(dir); // 确保文件夹存在

                            // 将代码写入文件，文件名为 类型名.cs
                            File.WriteAllText(Path.Combine(dir, t.Name + ".cs"), code, Encoding.UTF8);
                            cnt++;
                        }
                        return cnt; // 返回生成的文件总数
                    }

                // 指定模式
                case DecompMode.One:
                    {
                        // 指定类型模式必须提供类型名
                        if (string.IsNullOrEmpty(typeName))
                            throw new ArgumentException("指定类型模式必须提供 typeName");

                        // 查找指定全名的类型定义
                        var typeDef = dec.TypeSystem.MainModule.TypeDefinitions
                            .FirstOrDefault(t => t.FullName == typeName);

                        if (typeDef == null)
                            throw new ArgumentException($"类型 {typeName} 未找到");
                        // 反编译该类型并返回代码
                        return dec.DecompileType(typeDef.FullTypeName).ToString();
                    }
                default:
                    // 未知模式抛出异常
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        finally
        {
            // 确保临时文件被删除
            try { File.Delete(tempFile); } catch { }
        }
    }

    // 创建反编译器实例（复用此逻辑减少重复代码）
    private static CSharpDecompiler CreateDecomp(string tempPath, string origPath)
    {
        // 使用 PEFile 打开 DLL（using 会在方法结束时自动释放文件句柄）
        var pe = new PEFile(tempPath);
        // 检测 DLL 的目标框架标识（如 .NETCoreApp,Version=v8.0）
        string tf = pe.DetectTargetFrameworkId();
        // 创建程序集解析器，用于查找被反编译 DLL 所依赖的其他程序集
        var res = new UniversalAssemblyResolver(tempPath, false, tf);

        // 添加默认引用路径（复用 Compiler 类中的方法获取编译时使用的所有程序集目录）
        foreach (var p in GetRefPaths(false))
            res.AddSearchDirectory(Path.GetDirectoryName(p)!); // 将每个程序集所在的目录加入搜索路径

        // 添加当前 DLL 所在的目录（可能包含同目录下的其他依赖项）
        res.AddSearchDirectory(Path.GetDirectoryName(origPath)!);

        // 返回配置好的反编译器实例
        return new CSharpDecompiler(pe, res, new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false // 解析依赖失败时不抛出异常，继续尝试
        });
    }
    #endregion

    #region 提取内嵌资源
    private static void ExtractRes(string dllPath)
    {
        try
        {
            string fullPath = Path.IsPathRooted(dllPath) ? dllPath : Path.GetFullPath(dllPath);
            if (!File.Exists(fullPath))
            {
                TShock.Log.ConsoleWarn($"[提取资源] 文件不存在: {fullPath}");
                return;
            }

            using var pe = new PEFile(fullPath);
            var resources = pe.Resources;

            // 没有内嵌资源直接返回
            if (resources.IsEmpty) return;

            // 输出目录基于原始 DLL 名称，而非临时文件
            string outDir = Path.Combine(Configuration.Paths, "源码",
                Path.GetFileNameWithoutExtension(dllPath) + "_内嵌");
            Directory.CreateDirectory(outDir);

            int count = 0;
            foreach (var res in resources)
            {
                using var stream = res.TryOpenStream();
                if (stream == null) continue;

                string fileName = res.Name;
                string filePath = Path.Combine(outDir, fileName);
                string fileDir = Path.GetDirectoryName(filePath)!;
                if (!string.IsNullOrEmpty(fileDir))
                    Directory.CreateDirectory(fileDir);

                using var fs = File.Create(filePath);
                stream.CopyTo(fs);
                count++;
            }

            TShock.Log.ConsoleInfo($"发现 {count} 个内嵌资源，已提取到:\n {outDir}");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"[提取资源] 失败: {ex.Message}");
        }
    }
    #endregion

    #region 获取编译时使用的所有程序集路径（供反编译等模块调用,减少代码错误）
    public static List<string> GetRefPaths(bool forPlugin = false)
    {
        var paths = new HashSet<string>(); // 使用 HashSet 自动去重
        // 调用 Compiler 类中已有的方法添加 TShock 相关的引用路径
        Compiler.AddTShockReferences(paths, forPlugin);
        // 调用 Compiler 类中已有的方法添加系统运行时引用路径
        Compiler.AddSystemReferences(paths);
        return paths.ToList(); // 转换为 List 返回
    }
    #endregion

    #region 清理特性
    private static string CleanAttrs(string code)
    {
        // 移除所有 assembly 级别的特性
        code = Regex.Replace(code, @"^\s*\[assembly:.*?\]\r?\n", "", RegexOptions.Multiline | RegexOptions.Singleline);

        // 移除所有 module 级别的特性
        code = Regex.Replace(code, @"^\s*\[module:.*?\]\r?\n", "", RegexOptions.Multiline | RegexOptions.Singleline);

        // 移除 IL 注释
        code = Regex.Replace(code, @"^\s*//IL_.*?\r?\n", "", RegexOptions.Multiline);

        return code;
    }
    #endregion
}