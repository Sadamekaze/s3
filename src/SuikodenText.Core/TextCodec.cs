using System.Text;
using System.Text.RegularExpressions;
using static SuikodenText.Core.Files;

namespace SuikodenText.Core;

public static class TextCodec
{
    private static readonly Regex Tokens = new(@"\{CTRL:([0-9A-F]+)\}", RegexOptions.Compiled);
    public static (string Text, int End)? Read(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset >= data.Length) return null;
        int limit = Math.Min(offset + 8192, data.Length);
        var text = new StringBuilder();
        bool header = offset + 1 < data.Length && data[offset] == 17 && data[offset + 1] == 64;
        while (offset < limit)
        {
            byte c = data[offset];
            if (c == 0 && offset + 1 < data.Length && data[offset + 1] == 64)
            {
                if (header && offset + 2 < data.Length && data[offset + 2] == 10)
                {
                    text.Append("{CTRL:0040}"); offset += 2; header = false; continue;
                }
                return (text.ToString(), offset + 2);
            }
            if (c == 10 || c is >= 32 and < 127) { text.Append((char)c); offset++; continue; }
            int size = c switch { 1 or 5 or 6 or 8 => 3, 7 or 14 or 17 => 2, _ => 0 };
            if (size == 0 || offset + size > data.Length || data[offset + size - 1] != 64) return null;
            text.Append("{CTRL:").Append(Convert.ToHexString(data.Slice(offset, size))).Append('}');
            offset += size;
        }
        return null;
    }
    private static byte[] Plain(string text)
    {
        Check(!text.Contains("{CTRL:", StringComparison.Ordinal), "Malformed CTRL token.");
        Check(text.All(c => c == '\n' || c is >= ' ' and <= '~'), "Text must contain only ASCII characters and newlines.");
        return Encoding.ASCII.GetBytes(text);
    }
    public static byte[] Encode(string text, string source)
    {
        Check(text is not null && source is not null, "translation and source must be strings.");
        var matches = Tokens.Matches(text!);
        Check(matches.Select(m => m.Groups[1].Value).SequenceEqual(
            Tokens.Matches(source!).Select(m => m.Groups[1].Value)), "CTRL tokens were modified or reordered.");
        using var result = new MemoryStream();
        int pos = 0;
        foreach (Match match in matches)
        {
            result.Write(Plain(text![pos..match.Index]));
            result.Write(Convert.FromHexString(match.Groups[1].Value));
            pos = match.Index + match.Length;
        }
        result.Write(Plain(text![pos..])); result.WriteByte(0); result.WriteByte(64);
        byte[] bytes = result.ToArray();
        var read = Read(bytes, 0);
        Check(read is not null && read.Value.Text == text && read.Value.End == bytes.Length,
            "The name/dialog structure changed or the entry exceeds the 8192-byte parser limit.");
        return bytes;
    }
}
