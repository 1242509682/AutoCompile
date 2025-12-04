using System.Text;
using TShockAPI;
using Terraria;
using Microsoft.Xna.Framework;
using Terraria.Utilities;

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
}