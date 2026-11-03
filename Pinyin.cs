using static GlassBox.Native;

namespace GlassBox;

/// <summary>
/// 拼音首字母：输入 wx 能搜到“微信”，vsc 能搜到 Visual Studio Code。
/// 不带字典：一级汉字（3755 个常用字）在 GB2312 里本来就是按拼音排的，看编码落在哪个区间就知道首字母。
/// </summary>
internal static class Pinyin
{
    // 每个字母在 GB2312 一级汉字里的起始编码（没有 i、u、v 开头的字）
    static readonly int[] Starts =
    {
        0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE, 0xBBF7, 0xBFA6, 0xC0AC, 0xC2E8,
        0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA, 0xC8BB, 0xC8F6, 0xCBFA, 0xCDDA, 0xCEF4, 0xD1B9, 0xD4D1,
    };
    const string Letters = "abcdefghjklmnopqrstwxyz";
    const int LevelOneEnd = 0xD7F9;

    static readonly Dictionary<string, (string All, string Words)> Cache = new();

    /// <summary>
    /// All：每个汉字取首字母、英文数字原样保留（QQ音乐 → qqyy）；
    /// Words：每个词只取第一个字母（Google Chrome → gc，网易云音乐 → wyyyy）。
    /// </summary>
    public static (string All, string Words) Initials(string text)
    {
        if (Cache.TryGetValue(text, out var cached)) return cached;
        var all = new System.Text.StringBuilder(text.Length);
        var words = new System.Text.StringBuilder(text.Length);
        bool inWord = false;
        char previous = '\0';
        foreach (char ch in text)
        {
            if (ch < 128)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    char lower = char.ToLowerInvariant(ch);
                    all.Append(lower);
                    // 驼峰也算新词：GitHubDesktop → ghd
                    if (!inWord || char.IsUpper(ch) && char.IsLower(previous)) words.Append(lower);
                    inWord = true;
                }
                else inWord = false;
                previous = ch;
                continue;
            }
            inWord = false;
            previous = ch;
            char letter = Of(ch);
            if (letter == '\0') continue;
            all.Append(letter);
            words.Append(letter);
        }
        var result = (all.ToString(), words.ToString());
        if (Cache.Count > 4000) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    // 多音字按软件、文件名里最常见的读法（音乐的“乐”不是快乐的“乐”）
    static readonly Dictionary<char, char> Preferred = new() { ['乐'] = 'y', ['重'] = 'c', ['行'] = 'h' };

    static unsafe char Of(char ch)
    {
        if (Preferred.TryGetValue(ch, out char preferred)) return preferred;
        if (ch < 0x4E00 || ch > 0x9FA5) return '\0';
        byte* bytes = stackalloc byte[4];
        int n = WideCharToMultiByte(936, 0, &ch, 1, bytes, 4, IntPtr.Zero, IntPtr.Zero);
        if (n != 2) return '\0';
        int code = bytes[0] << 8 | bytes[1];
        if (code < Starts[0] || code > LevelOneEnd) return '\0';
        int i = Starts.Length - 1;
        while (code < Starts[i]) i--;
        return Letters[i];
    }
}
