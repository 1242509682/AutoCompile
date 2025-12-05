using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using TShockAPI;

namespace AutoCompile;

internal class LogsMag
{
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

    #region 日志方法
    public static void LogCompile(string ns, string dllPath, string pdbPath)
    {
        try
        {
            var log = new StringBuilder();
            log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            log.AppendLine($"命名空间: {ns}");
            log.AppendLine($"DLL文件: {Path.GetFileName(dllPath)}");
            log.AppendLine($"文件大小: {new FileInfo(dllPath).Length / 1024} KB");

            if (pdbPath != null && File.Exists(pdbPath))
            {
                log.AppendLine($"PDB文件: {Path.GetFileName(pdbPath)}");
            }

            log.AppendLine();

            TShock.Log.Debug(log.ToString());
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录编译日志失败: {ex.Message}");
        }
    }

    public static void LogSkip(List<string> skipped, List<string> errors)
    {
        try
        {
            var log = new StringBuilder();
            log.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");

            if (skipped.Count > 0)
            {
                log.AppendLine($"跳过 {skipped.Count} 个文件:");
                foreach (var file in skipped.Take(10))
                    log.AppendLine($"  {file}");

                if (skipped.Count > 10)
                    log.AppendLine($"  ... 等{skipped.Count}个文件");
            }

            if (errors.Count > 0)
            {
                log.AppendLine($"错误 {errors.Count} 个文件:");
                foreach (var file in errors.Take(10))
                    log.AppendLine($"  {file}");

                if (errors.Count > 10)
                    log.AppendLine($"  ... 等{errors.Count}个文件");
            }

            log.AppendLine();

            TShock.Log.Info(log.ToString());
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"【自动编译】 记录跳过文件日志失败: {ex.Message}");
        }
    }
    #endregion

    #region 错误处理
    public static CompResult ErrorMess(string pluginName, EmitResult er)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"❌ 编译失败 [{pluginName}]");
            sb.AppendLine("-".PadRight(40, '-'));

            // 添加内存信息
            var memInfo = LogsMag.GetMemInfo();
            sb.AppendLine($"编译时内存: {memInfo}");
            sb.AppendLine();

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
            LogErrFile(pluginName, errs);

            return CompResult.Fail("编译失败");
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError($"❌ 编译失败 [{pluginName}]");
            TShock.Log.ConsoleError($"错误异常: {ex.Message}");
            return CompResult.Fail("编译失败");
        }
    }
    #endregion

    #region 记录错误到日志文件 - 使用using语句
    private static void LogErrFile(string grpName, List<Diagnostic> errs)
    {
        try
        {
            var logDir = Path.Combine(Configuration.Paths, "编译日志");
            if (!Directory.Exists(logDir))
                Directory.CreateDirectory(logDir);

            var safeName = Utils.CleanName(grpName);
            var logFile = Path.Combine(logDir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            using (var writer = new StreamWriter(logFile, false, Encoding.UTF8, 4096))
            {
                writer.WriteLine($"编译错误日志 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                writer.WriteLine($"组名: {grpName}");
                writer.WriteLine($"总错误数: {errs.Count}");
                writer.WriteLine("=".PadRight(80, '='));

                // 按照错误信息进行分组（清理后的中文错误信息）
                var errorGroups = errs
                    .Select(err => new
                    {
                        Diagnostic = err,
                        CleanMsg = CleanErrMsg(err.GetMessage()),
                        OrigMsg = err.GetMessage()
                    })
                    .GroupBy(e => e.CleanMsg)
                    .ToList();

                int groupIndex = 0;
                foreach (var group in errorGroups)
                {
                    groupIndex++;
                    writer.WriteLine($"\n[第 {groupIndex} 类错误] 共 {group.Count()} 处");
                    writer.WriteLine("-".PadRight(80, '-'));

                    // 显示错误位置
                    writer.WriteLine("错误位置:");
                    foreach (var err in group)
                    {
                        var loc = err.Diagnostic.Location;
                        string lineInfo = "";
                        if (loc.SourceTree != null && loc.GetLineSpan().IsValid)
                        {
                            var lineSpan = loc.GetLineSpan();
                            lineInfo = $"行 {lineSpan.StartLinePosition.Line + 1}";
                        }
                        var fileName = Path.GetFileName(loc.SourceTree?.FilePath ?? "Unknown");
                        writer.WriteLine($"  {fileName} {lineInfo}");
                    }

                    writer.WriteLine();
                    writer.WriteLine("错误内容:");

                    // 根据配置显示英文和中文
                    bool showEnglish = AutoCompile.Config.ShowErrorEnglish;
                    bool showChinese = AutoCompile.Config.ShowErrorChinese;

                    // 如果两个都为false，默认显示英文
                    if (!showEnglish && !showChinese)
                    {
                        showEnglish = true;
                    }

                    if (showEnglish)
                    {
                        writer.WriteLine($"(EN): {group.First().OrigMsg}");
                    }

                    if (showChinese && !string.IsNullOrEmpty(group.Key))
                    {
                        writer.WriteLine($"(CN): {group.Key}");
                    }

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
            ("because", "因为"),
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
            // 新增翻译
            ("'await' requires that the type", "'await' 要求类型"),
            ("must be a non-abstract type with a public parameterless constructor", "必须是非抽象类型且具有公共无参数构造函数"),
            ("cannot be used as a constant", "不能用作常量"),
            ("is not supported by the language", "语言不支持"),
            ("does not have a predefined size, therefore sizeof can only be used in an unsafe context", "没有预定义的大小，因此 sizeof 只能在 unsafe 上下文中使用"),
            ("Anonymous methods cannot be converted to expression trees", "匿名方法不能转换为表达式树"),
            ("The call is ambiguous between the following methods or properties", "以下方法或属性之间的调用不明确"),
            ("'object' does not contain a definition for", "'object' 不包含定义"),
            ("The best overloaded method match for", "最佳重载方法匹配"),
            ("has some invalid arguments", "有一些无效参数"),
            ("No overload for method", "方法没有重载"),
            ("takes", "接受"),
            ("arguments", "参数"),
            ("cannot convert from", "无法从"),
            ("to", "转换为"),
            ("and", "和"),
            ("An object reference is required for the non-static field, method, or property", "非静态字段、方法或属性需要对象引用"),
            ("Missing compiler required member", "缺少编译器所需的成员"),
            ("The feature", "该功能"),
            ("is not available in C#", "在C#中不可用"),
            ("Please use language version", "请使用语言版本"),
            ("or greater", "或更高版本"),
            ("The left-hand side of an assignment must be a variable, property or indexer", "赋值操作的左侧必须是变量、属性或索引器"),
            ("The right-hand side of a", "右侧"),
            ("expression must be a lambda expression", "表达式必须是 lambda 表达式"),
            ("The expression must be of type", "表达式必须为类型"),
            ("because it is being assigned to", "因为它被分配给"),
            ("The using directive for", "using 指令"),
            ("appeared previously in this namespace", "在此命名空间中已出现过"),
            ("The namespace", "命名空间"),
            ("already contains a definition for", "已包含定义"),
            ("The modifier", "修饰符"),
            ("is not valid for this item", "对此项无效"),
            ("Member names cannot be the same as their enclosing type", "成员名不能与其封闭类型相同"),
            ("Partial declarations of", "部分声明"),
            ("must have the same type parameter names in the same order", "必须具有相同的类型参数名称和顺序"),
            ("A previous catch clause already catches all exceptions of this or a super type", "前面的 catch 子句已捕获此类型或超类型的所有异常"),
            ("The break statement can only be used within a loop or switch", "break 语句只能在循环或 switch 中使用"),
            ("The continue statement can only be used within a loop", "continue 语句只能在循环中使用"),
            ("The yield statement cannot be used inside an anonymous method or lambda expression", "yield 语句不能在匿名方法或 lambda 表达式内使用"),
            ("The yield statement can only be used in an iterator block", "yield 语句只能在迭代器块中使用"),
            ("The return type of an async method must be void, Task or Task<T>", "异步方法的返回类型必须为 void、Task 或 Task<T>"),
            ("The 'await' operator can only be used within an async method", "'await' 运算符只能在异步方法中使用"),
            ("Async methods cannot have ref or out parameters", "异步方法不能有 ref 或 out 参数"),
            ("The 'fixed' statement can only be used in an unsafe context", "'fixed' 语句只能在 unsafe 上下文中使用"),
            ("Pointers and fixed size buffers may only be used in an unsafe context", "指针和固定大小缓冲区只能在 unsafe 上下文中使用"),
            ("Array size cannot be specified in a variable declaration", "不能在变量声明中指定数组大小"),
            ("A field initializer cannot reference the non-static field, method, or property", "字段初始值设定项不能引用非静态字段、方法或属性"),
            ("A static class cannot contain non-static members", "静态类不能包含非静态成员"),
            ("Cannot create an instance of the static class", "无法创建静态类的实例"),
            ("'base' is not available in the current context", "'base' 在当前上下文中不可用"),
            ("'this' is not available in the current context", "'this' 在当前上下文中不可用"),
            ("The name does not exist in the current context", "该名称在当前上下文中不存在"),
            ("The variable is being used without being assigned", "该变量在使用前未赋值"),
            ("Use of unassigned local variable", "使用了未赋值的局部变量"),
            ("Cannot assign to", "无法分配给"),
            ("because it is a", "因为它是一个"),
            ("'readonly' field cannot be assigned to (except in a constructor or init-only setter of the type in which the field is defined)", "'readonly' 字段不能赋值（除非在定义该字段的类型的构造函数或 init-only setter 中）"),
            ("'const' field cannot be assigned to", "'const' 字段不能赋值"),
            ("'using' statement requires an expression of type 'IDisposable'", "'using' 语句需要类型为 'IDisposable' 的表达式"),
            ("'lock' statement requires an expression of reference type", "'lock' 语句需要引用类型的表达式"),
            ("'foreach' statement requires an expression of a type that implements 'IEnumerable' or 'IEnumerable<T>'", "'foreach' 语句需要实现 'IEnumerable' 或 'IEnumerable<T>' 的类型的表达式"),
            ("'for' loop requires an expression of a type that implements 'IEnumerator'", "'for' 循环需要实现 'IEnumerator' 的类型的表达式"),
            ("'while' statement requires an expression of type 'bool'", "'while' 语句需要类型为 'bool' 的表达式"),
            ("'if' statement requires an expression of type 'bool'", "'if' 语句需要类型为 'bool' 的表达式"),
            ("'do-while' statement requires an expression of type 'bool'", "'do-while' 语句需要类型为 'bool' 的表达式"),
            ("'switch' statement requires an expression of integral type or string type", "'switch' 语句需要整数类型或字符串类型的表达式"),
            ("'case' label must be a constant expression", "'case' 标签必须是常量表达式"),
            ("'goto case' statement requires an expression of integral type", "'goto case' 语句需要整数类型的表达式"),
            ("'throw' statement requires an expression of type 'Exception'", "'throw' 语句需要类型为 'Exception' 的表达式"),
            ("'return' statement requires an expression of type", "'return' 语句需要类型为"),
            ("'yield return' statement requires an expression of type", "'yield return' 语句需要类型为"),
            ("'yield break' statement cannot be used in a method that returns a value", "'yield break' 语句不能在返回值的方法中使用"),
            ("'await' statement requires an expression of type", "'await' 语句需要类型为"),
            ("'async' modifier can only be used on methods that return void, Task or Task<T>", "'async' 修饰符只能用于返回 void、Task 或 Task<T> 的方法"),
            ("'unsafe' modifier can only be used on methods, types, and fields", "'unsafe' 修饰符只能用于方法、类型和字段"),
            ("'fixed' modifier can only be used on fields of an array type", "'fixed' 修饰符只能用于数组类型的字段"),
            ("'volatile' modifier can only be used on fields", "'volatile' 修饰符只能用于字段"),
            ("'override' modifier can only be used on methods, properties, indexers, and events", "'override' 修饰符只能用于方法、属性、索引器和事件"),
            ("'sealed' modifier can only be used on override methods", "'sealed' 修饰符只能用于重写方法"),
            ("'abstract' modifier can only be used on classes, methods, properties, indexers, and events", "'abstract' 修饰符只能用于类、方法、属性、索引器和事件"),
            ("'virtual' modifier can only be used on methods, properties, indexers, and events", "'virtual' 修饰符只能用于方法、属性、索引器和事件"),
            ("'extern' modifier can only be used on methods", "'extern' 修饰符只能用于方法"),
            ("'static' modifier can only be used on classes, interfaces, structs, enums, delegates, and members", "'static' 修饰符只能用于类、接口、结构、枚举、委托和成员"),
            ("'readonly' modifier can only be used on fields and structs", "'readonly' 修饰符只能用于字段和结构"),
            ("'const' modifier can only be used on fields and locals", "'const' 修饰符只能用于字段和局部变量"),
            ("'event' modifier can only be used on delegate types", "'event' 修饰符只能用于委托类型"),
            ("'delegate' modifier can only be used on delegate types", "'delegate' 修饰符只能用于委托类型"),
            ("'enum' modifier can only be used on enums", "'enum' 修饰符只能用于枚举"),
            ("'interface' modifier can only be used on interfaces", "'interface' 修饰符只能用于接口"),
            ("'struct' modifier can only be used on structs", "'struct' 修饰符只能用于结构"),
            ("'class' modifier can only be used on classes", "'class' 修饰符只能用于类"),
            ("'new' modifier can only be used on members", "'new' 修饰符只能用于成员"),
            ("'partial' modifier can only be used on classes, structs, interfaces, and methods", "'partial' 修饰符只能用于类、结构、接口和方法"),
            ("'async' modifier can only be used on methods", "'async' 修饰符只能用于方法"),
            ("'unsafe' modifier can only be used on types and members", "'unsafe' 修饰符只能用于类型和成员"),
            ("'fixed' modifier can only be used on fields", "'fixed' 修饰符只能用于字段"),
            ("'volatile' modifier can only be used on fields", "'volatile' 修饰符只能用于字段"),
            ("'override' modifier can only be used on members", "'override' 修饰符只能用于成员"),
            ("'sealed' modifier can only be used on classes and members", "'sealed' 修饰符只能用于类和成员"),
            ("'abstract' modifier can only be used on classes and members", "'abstract' 修饰符只能用于类和成员"),
            ("'virtual' modifier can only be used on members", "'virtual' 修饰符只能用于成员"),
            ("'extern' modifier can only be used on methods", "'extern' 修饰符只能用于方法"),
            ("'static' modifier can only be used on members", "'static' 修饰符只能用于成员"),
            ("'readonly' modifier can only be used on fields", "'readonly' 修饰符只能用于字段"),
            ("'const' modifier can only be used on fields and locals", "'const' 修饰符只能用于字段和局部变量"),
            ("'event' modifier can only be used on events", "'event' 修饰符只能用于事件"),
            ("'delegate' modifier can only be used on delegates", "'delegate' 修饰符只能用于委托"),
            ("'enum' modifier can only be used on enums", "'enum' 修饰符只能用于枚举"),
            ("'interface' modifier can only be used on interfaces", "'interface' 修饰符只能用于接口"),
            ("'struct' modifier can only be used on structs", "'struct' 修饰符只能用于结构"),
            ("'class' modifier can only be used on classes", "'class' 修饰符只能用于类"),
            ("'new' modifier can only be used on members", "'new' 修饰符只能用于成员"),
            ("'partial' modifier can only be used on types and methods", "'partial' 修饰符只能用于类型和方法"),
            ("foreach statement cannot operate on variables of type", "foreach语句不能对类型为的变量进行操作"),
            ("does not contain a public instance or extension definition for", "不包含的公共实例或扩展定义"),
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

    #region 获取内存信息
    public static string GetMemInfo()
    {
        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            long memUsed = GC.GetTotalMemory(false) / 1024 / 1024;
            long workingSet = process.WorkingSet64 / 1024 / 1024;

            return $"当前内存: {memUsed}MB | 工作集: {workingSet}MB";
        }
        catch
        {
            return "内存信息获取失败";
        }
    }
    #endregion
}