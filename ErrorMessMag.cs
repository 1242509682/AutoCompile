using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using TShockAPI;

namespace AutoCompile;

internal class ErrorMessMag
{
    #region 错误处理
    public static CompResult ErrorMess(KeyValuePair<string, Compiler.CompilationGroup> g, EmitResult er)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"❌ 编译失败 [{g.Key}]");
            sb.AppendLine("-".PadRight(40, '-'));

            // 获取错误
            var errs = er.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            // 只显示错误数量
            sb.AppendLine($"发现 {errs.Count} 个错误");

            sb.AppendLine("\n🔧 解决建议：");
            sb.AppendLine("  1. 检查「程序集」文件夹");
            sb.AppendLine("  2. 确保 using 语句正确");
            sb.AppendLine("  3. 检查源码文件是否完整");
            sb.AppendLine("  4. 查看日志文件");

            // 记录到控制台
            TShock.Log.ConsoleError(sb.ToString());

            // 记录到日志文件
            LogErrFile(g.Key, errs);

            return CompResult.Fail("编译失败");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"❌ 编译失败 [{g.Key}]");
            TShock.Log.ConsoleError($"错误异常: {ex.Message}");
            return CompResult.Fail("编译失败");
        }
    }
    #endregion

    #region 记录错误到日志文件
    private static void LogErrFile(string grpName, List<Diagnostic> errs)
    {
        try
        {
            var logDir = Path.Combine(Configuration.Paths, "编译日志");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            // 安全文件名
            var safeName = Utils.CleanName(grpName);
            var logFile = Path.Combine(logDir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            using var writer = new StreamWriter(logFile, false, Encoding.UTF8);
            writer.WriteLine($"编译错误日志 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"组名: {grpName}");
            writer.WriteLine($"总错误数: {errs.Count}");
            writer.WriteLine("=".PadRight(80, '='));

            // 按文件分组
            var fileGrps = errs.GroupBy(e => e.Location.SourceTree?.FilePath ?? "Unknown");

            foreach (var fileGrp in fileGrps)
            {
                var fileName = Path.GetFileName(fileGrp.Key);
                writer.WriteLine($"\n文件: {fileName} ({fileGrp.Count()} 个错误)");
                writer.WriteLine("-".PadRight(80, '-'));

                int cnt = 0;
                foreach (var err in fileGrp.Take(100))
                {
                    cnt++;

                    var loc = err.Location;
                    var origMsg = err.GetMessage(); // 原始英文错误信息
                    var clearMsg = CleanErrMsg(origMsg); // 清理和翻译后的信息

                    // 行号
                    string lineInfo = "";
                    if (loc.SourceTree != null && loc.GetLineSpan().IsValid)
                    {
                        var lineSpan = loc.GetLineSpan();
                        lineInfo = $"行 {lineSpan.StartLinePosition.Line + 1}";
                    }

                    // 同时显示英文和中文
                    writer.WriteLine($"[{cnt}] {lineInfo} (EN): {origMsg}");
                    if (!string.IsNullOrEmpty(clearMsg) && clearMsg != origMsg)
                    {
                        writer.WriteLine($"[{cnt}] {lineInfo} (CN): {clearMsg}");
                    }

                    // 每个错误之间空一行
                    writer.WriteLine();
                }
            }

            TShock.Log.ConsoleInfo($"📋 错误日志:");
            TShock.Log.ConsoleInfo($"   {logFile}");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleWarn($"保存日志失败: {ex.Message}");
        }
    }
    #endregion

    #region 清理错误信息
    private static string CleanErrMsg(string errMsg)
    {
        if (string.IsNullOrEmpty(errMsg))
            return errMsg;

        // 移除
        errMsg = Regex.Replace(errMsg, @", Culture=[^,]+", "");
        errMsg = Regex.Replace(errMsg, @", PublicKeyToken=[^,]+", "");

        // 将单引号 ' 替换为中文括号【】
        errMsg = ReplaceQuotes(errMsg);

        // 翻译行尾括号内的内容
        errMsg = TranslateParentheses(errMsg);

        return errMsg.Trim();
    }
    #endregion

    #region 翻译英文内容
    private static string TranslateParentheses(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // 使用正则表达式匹配括号内的英文内容并翻译
        text = Regex.Replace(text,
            @"\(are you missing a using directive or an assembly reference\?\)",
            "（是否缺少 using 指令或程序集引用？）");

        text = Regex.Replace(text,
            @"\(are you missing an assembly reference\?\)",
            "（是否缺少程序集引用？）");

        text = Regex.Replace(text,
            @"\(are you missing a using directive\?\)",
            "（是否缺少 using 指令？）");

        // 首先处理带数字占位符的翻译
        text = Regex.Replace(text,
            @"does not contain a constructor that takes (\d+) arguments",
            match =>
            {
                var num = int.Parse(match.Groups[1].Value);
                return $"不包含接受{num}个参数的构造函数";
            });

        // 翻译其他常见短语
        var take = new (string, string)[]
        {
            ("Version=", "版本号"),
            ("Operator", "操作符"),
            ("The name", "该类型"),
            ("The type", "该类型"),
            ("The type name", "该类型"),
            ("could be found", "被找到"),
            ("does not exist", "不存在"),
            ("in the namespace", "这个命名空间"),
            ("in the current context", "当前上下文"),
            ("could not be found", "找不到"),
            ("The type or namespace name", "该类型或命名空间名称"),
            ("cannot be applied to operands of type", "不能应用于类型为的操作数"),
            ("does not contain a definition for", "不包含定义"),
            ("and no accessible extension method", "且没有可访问的扩展方法"),
            ("accepting a first argument of type", "接受类型为的第一个参数"),
            ("This type has been forwarded to assembly", "此类型已转发到程序集"),
            ("Consider adding a reference to that assembly", "请考虑添加对该程序集的引用"),
            ("You must add a reference to assembly", "必须添加对程序集"),
            ("is defined in an assembly that is not referenced", "在未引用的程序集中定义"),
            ("is a method, which is not valid in the given context", "是一种方法,这在给定的上下文中无效"),
            ("is inaccessible due to its protection level", "由于其保护级别，无法访问"),
            ("is an ambiguous reference between", "以下两者之间存在模糊引用"),
            ("does not implement interface member", "无法实现接口成员"),
            ("A global using directive must precede all non-global using directives.", "不要把bin或obj放进【源码】文件夹"),
            ("Duplicate", "不要把bin或obj放进【源码】文件夹"),
            ("System.Reflection.AssemblyCompanyAttribute", "不要把bin或obj放进【源码】文件夹"),

        }.OrderByDescending(t => t.Item1.Length).ToList();

        foreach (var (from, to) in take)
        {
            text = text.Replace(from, to);
        }

        return text;
    }
    #endregion

    #region 将单引号替换为中文括号
    private static string ReplaceQuotes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // 使用正则表达式匹配所有单引号中的内容
        return Regex.Replace(text, @"'([^']*)'", "【$1】");
    }
    #endregion
}