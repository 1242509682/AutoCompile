using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Utilities;
using TShockAPI;

namespace AutoCompile;

internal class Utils
{
    public static UnifiedRandom Rand = Main.rand;

    #region 颜色定义
    public static Color color1 = new(166, 213, 234);
    public static Color color2 = new(245, 247, 175);
    #endregion

    #region 渐变色消息
    public static void GradMess(TSPlayer plr, StringBuilder msg)
    {
        var text = msg.ToString();
        var lines = text.Split('\n');

        var result = new StringBuilder();
        var start = color1;
        var end = color2;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!string.IsNullOrEmpty(lines[i]))
            {
                float ratio = (float)i / Math.Max(lines.Length - 1, 1);
                var color = Color.Lerp(start, end, ratio);
                var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
                result.AppendLine($"[c/{hex}:{lines[i]}]");
            }
        }

        plr.SendMessage(result.ToString(), start);
    }
    #endregion

    #region 清理文件名
    public static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Global";

        var invalid = Path.GetInvalidFileNameChars();
        var result = new StringBuilder();

        foreach (char c in name)
        {
            if (!invalid.Contains(c))
                result.Append(c);
            else
                result.Append('_');
        }

        var cleaned = result.ToString().Trim();
        return string.IsNullOrEmpty(cleaned) ? "Global" : cleaned;
    }
    #endregion

    #region 检查是否是C#代码 
    public static bool IsValidCSharpCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            // 尝试解析，让编译器决定是否有效
            var tree = CSharpSyntaxTree.ParseText(code);

            // 如果有语法错误，ParseText可能不会抛出异常
            // 但会包含诊断信息
            var diagnostics = tree.GetDiagnostics();

            // 如果没有严重错误（则认为可用)
            return !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }
    #endregion

    #region 获取C#版本号 .net 6对应的是CSharp11
    public static LanguageVersion GetLangVer()
    {
        return AutoCompile.Config.LangVer switch
        {
            "CSharp8" => LanguageVersion.CSharp8,
            "CSharp9" => LanguageVersion.CSharp9,
            "CSharp10" => LanguageVersion.CSharp10,
            "CSharp11" => LanguageVersion.CSharp11,
            "CSharp12" => LanguageVersion.CSharp12,
            "CSharp13" => LanguageVersion.CSharp13,
            "CSharp14" => LanguageVersion.CSharp14,
            _ => LanguageVersion.Latest
        };
    }
    #endregion

    #region 检查文件数量和大小
    public static CompResult CheckFileSize(string path)
    {
        try
        {
            var files = Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories);

            // 检查文件数量
            if (files.Length > AutoCompile.Config.MaxFiles)
            {
                return CompResult.Fail($"文件过多: {files.Length} > {AutoCompile.Config.MaxFiles}");
            }

            // 检查文件大小
            long totalSize = 0;
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                totalSize += info.Length;
            }

            var totalMB = totalSize / 1024 / 1024;
            if (totalMB > AutoCompile.Config.MaxSizeMB)
            {
                TShock.Log.ConsoleWarn($"文件过大: {totalMB}MB > {AutoCompile.Config.MaxSizeMB}MB");
                return CompResult.Fail($"文件过大: {totalMB}MB > {AutoCompile.Config.MaxSizeMB}MB");
            }

            return CompResult.Success();
        }
        catch (Exception ex)
        {
            return CompResult.Fail($"安全检查异常: {ex.Message}");
        }
    }
    #endregion

    #region 检查DLL有效性
    public static bool IsValidDll(string dllPath)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(dllPath);
            return name != null;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (FileLoadException)
        {
            return false;
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 检查程序集失败 {Path.GetFileName(dllPath)}: {ex.Message}");
            return false;
        }
    }
    #endregion

    #region 格式化 using
    public static string FmtUsings(List<string> usgs)
    {
        if (usgs == null || usgs.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var usg in usgs)
        {
            if (string.IsNullOrWhiteSpace(usg))
                continue;

            var trim = usg.Trim();

            // 已完整
            if (trim.StartsWith("using ") && trim.EndsWith(";"))
            {
                sb.AppendLine(trim);
            }
            // 需补充
            else
            {
                sb.AppendLine($"using {trim};");
            }
        }

        return sb.ToString();
    }
    #endregion

    #region 提取代码中已有的 using 指令
    public static List<string> GetExistUsings(string code)
    {
        var usings = new List<string>();

        try
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();

            var Directives = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.ToString())
                .ToList();

            usings.AddRange(Directives);
        }
        catch
        {
            // 解析失败时使用简单方法
            var lines = code.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";"))
                {
                    usings.Add(trimmed);
                }
            }
        }

        return usings;
    }
    #endregion

    #region 过滤掉重复的 using
    public static string FilterUsings(string fmtUsgs, List<string> exist)
    {
        var lines = fmtUsgs.Split('\n');
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trim = line.Trim();

            // 取命名空间部分
            string nsOnly = GetNs(trim);

            // 查重复
            bool existFlag = exist.Any(ex =>
                SameNs(ex.Trim(), trim) || SameNs(ex.Trim(), nsOnly));

            if (!existFlag)
                result.AppendLine(trim);
        }

        return result.ToString();
    }

    // 取命名空间
    public static string GetNs(string usgStmt)
    {
        if (string.IsNullOrWhiteSpace(usgStmt))
            return string.Empty;

        var trim = usgStmt.Trim();

        if (trim.StartsWith("using "))
            trim = trim.Substring(6);

        if (trim.EndsWith(";"))
            trim = trim.Substring(0, trim.Length - 1);

        return trim.Trim();
    }

    // 比命名空间
    public static bool SameNs(string ex, string now)
    {
        var exNs = GetNs(ex);
        var nowNs = GetNs(now);

        return string.Equals(exNs, nowNs, StringComparison.OrdinalIgnoreCase);
    }
    #endregion

    #region 提取插件名称
    public static string GetPluginName(List<SyntaxTree> trees)
    {
        try
        {
            // 遍历组中的所有文件，查找插件信息
            foreach (var tree in trees)
            {
                var root = tree.GetRoot();

                // 查找继承自TerrariaPlugin的主类
                var MainClass = root.DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(cls => cls.BaseList?.Types
                        .Any(t => t.Type.ToString().Contains("TerrariaPlugin")) == true);

                if (MainClass != null)
                {
                    // 查找Name属性
                    var nameProp = MainClass.DescendantNodes()
                        .OfType<PropertyDeclarationSyntax>()
                        .FirstOrDefault(p => p.Identifier.Text == "Name");

                    // 提取插件名称
                    if (nameProp is null) continue;

                    string name = NameFromProperty(nameProp, MainClass);

                    if (!string.IsNullOrEmpty(name))
                    {
                        TShock.Log.ConsoleInfo($"【自动编译】 在{Path.GetFileName(tree.FilePath)}中获取到插件名: {name}");
                        return name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 提取插件信息失败: {ex.Message}");
        }

        return string.Empty;
    }

    // 从属性中提取名称
    private static string NameFromProperty(PropertyDeclarationSyntax prop, ClassDeclarationSyntax cls)
    {
        if (prop == null) return cls.Identifier.Text;

        // 箭头函数形式
        if (prop.ExpressionBody?.Expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        // Getter形式
        var getter = prop.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.Text == "get");
        if (getter?.Body?.Statements.FirstOrDefault() is ReturnStatementSyntax Stmt &&
            Stmt.Expression is LiteralExpressionSyntax Expr)
        {
            return Expr.Token.ValueText;
        }

        return cls.Identifier.Text;
    }
    #endregion

    #region 编码检测和修复
    public static string ReadAndFixFile(string code)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(code);

            // 首先尝试最可能的中文编码
            string[] Names = new string[] { "UTF-8", "GBK", "GB2312", "GB18030", "Big5", "Windows-1252" };
            var count = 0;
            var n = "";
            foreach (string name in Names)
            {
                try
                {
                    Encoding ed = Encoding.GetEncoding(name);
                    string text = ed.GetString(bytes);

                    // 检查是否有明显的乱码
                    if (!text.Contains("�") && !text.Contains("��"))
                    {
                        count++;
                        n = name;
                        return text;
                    }
                }
                catch
                {
                    continue;
                }
            }

            if (count > 0)
            {
                TShock.Log.ConsoleInfo($"【自动编译】 使用编码: {n}");
            }

            // 如果都不行，使用系统默认编码
            return Encoding.Default.GetString(bytes);
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 读取文件失败，使用UTF-8: {ex.Message}");
            return File.ReadAllText(code, Encoding.UTF8);
        }
    }
    #endregion

    #region 删除编译输出文件
    public static void CleanOutFiles()
    {
        try
        {
            var outDir = Path.Combine(Configuration.Paths, "编译输出");
            if (Directory.Exists(outDir))
            {
                // 先删除不锁定的文件
                var dllFiles = Directory.GetFiles(outDir, "*.dll");
                var pdbFiles = Directory.GetFiles(outDir, "*.pdb");

                foreach (var file in dllFiles)
                {
                    try
                    {
                        File.Delete(file);
                        TShock.Log.ConsoleInfo($"【自动编译】 删除: {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleError($"【自动编译】 删除文件失败 {file}: {ex.Message}");
                    }
                }

                foreach (var file in pdbFiles)
                {
                    try
                    {
                        File.Delete(file);
                        TShock.Log.ConsoleInfo($"【自动编译】 删除: {Path.GetFileName(file)}");
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleError($"【自动编译】 删除PDB文件失败 {file}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 清理文件失败: {ex.Message}");
        }
    }
    #endregion

    #region 删除源码文件
    public static void CleanCodeFiles()
    {
        try
        {
            var CodeDir = Path.Combine(Configuration.Paths, "源码");
            if (Directory.Exists(CodeDir))
            {
                // 获取所有文件
                var all = Directory.GetFiles(CodeDir, "*.*", SearchOption.AllDirectories);
                int count = 0;

                foreach (var file in all)
                {
                    try
                    {
                        File.Delete(file);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleError($"【自动编译】 删除文件失败 {file}: {ex.Message}");
                    }
                }

                if (count > 0)
                {
                    TShock.Log.ConsoleInfo($"【自动编译】 清理完成，共删除 {count} 个文件");
                }
            }
            else
            {
                TShock.Log.ConsoleInfo($"【自动编译】 源码目录不存在: {CodeDir}");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 清理源码文件失败: {ex.Message}");
        }
    }
    #endregion

    #region 获取错误所在文件名
    public static string GetFileName(Diagnostic diagnostic)
    {
        try
        {
            var location = diagnostic.Location;
            if (location.SourceTree != null && !string.IsNullOrEmpty(location.SourceTree.FilePath))
            {
                return Path.GetFileName(location.SourceTree.FilePath);
            }
            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
    #endregion

    #region 编译成功后清理日志文件
    public static void ClearLogs()
    {
        // 检查配置是否启用清理
        if (!AutoCompile.Config.ClearLogs) return;

        try
        {
            var logDir = Path.Combine(Configuration.Paths, "编译日志");
            if (!Directory.Exists(logDir))
                return;

            // 获取所有日志文件
            var logFiles = Directory.GetFiles(logDir, "*.txt", SearchOption.AllDirectories);
            if (logFiles.Length == 0)
                return;

            int count = 0;
            foreach (var logFile in logFiles)
            {
                File.Delete(logFile);
                count++;
            }

            if (count > 0)
            {
                TShock.Log.ConsoleInfo($"【自动编译】 清理了 {count} 个编译日志文件");
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"【自动编译】 清理编译日志失败: {ex.Message}");
        }
    }
    #endregion

}