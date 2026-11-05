namespace Dnf.Net.Tests;

/// <summary>读 Golden 目录里制表符分隔的向量文件:跳过空行与 # 注释行。</summary>
internal static class FrameVectorFile
{
    public static List<string[]> Rows(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Golden", fileName);
        List<string[]> rows = new();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            rows.Add(line.Split('\t'));
        }
        return rows;
    }

    public static byte[] Hex(string text)
    {
        if (text == "-" || text.Length == 0)
        {
            return Array.Empty<byte>();
        }
        return Convert.FromHexString(text);
    }

    public static uint HexU32(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(2);
        }
        return Convert.ToUInt32(text, 16);
    }
}
