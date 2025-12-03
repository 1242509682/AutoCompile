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
}