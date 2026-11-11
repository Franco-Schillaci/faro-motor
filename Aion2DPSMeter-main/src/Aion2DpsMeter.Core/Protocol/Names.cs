using System.Text;

namespace Aion2DpsMeter.Core.Protocol;

/// <summary>Rules for recognising character names inside binary records.</summary>
public static class Names
{
    /// <summary>Byte lengths a length-prefixed name field can have: 1-12 characters of up to 4 UTF-8 bytes.</summary>
    public const int MinFieldBytes = 1;
    public const int MaxFieldBytes = 48;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool IsNameFieldLength(int length) => length >= MinFieldBytes && length <= MaxFieldBytes;

    public static string? DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// A character name read from a field whose length the packet states: 1 to 12 letters or
    /// digits in any script, at least one letter. The whole field must be the name.
    /// </summary>
    public static string? Exact(ReadOnlySpan<byte> field)
    {
        var name = DecodeUtf8(field);
        if (name is null)
            return null;
        var runes = name.EnumerateRunes().ToList();
        if (runes.Count < 1 || runes.Count > 12)
            return null;
        if (!runes.All(Rune.IsLetterOrDigit) || !runes.Any(Rune.IsLetter))
            return null;
        return name;
    }

    /// <summary>
    /// A name found by a heuristic scan: the leading run of letters/digits, not all digits,
    /// and at least two characters unless it contains CJK.
    /// </summary>
    public static string? Sanitize(string nickname)
    {
        int nul = nickname.IndexOf('\0');
        var trimmed = (nul >= 0 ? nickname[..nul] : nickname).Trim();
        if (trimmed.Length == 0)
            return null;

        var sb = new StringBuilder();
        bool onlyNumbers = true;
        bool hasCjk = false;
        int count = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune) || rune.Value == 0xFFFD || Rune.IsControl(rune))
            {
                if (sb.Length == 0)
                    return null;
                break;
            }
            sb.Append(rune.ToString());
            count++;
            if (Rune.IsLetter(rune))
                onlyNumbers = false;
            if (IsCjk(rune.Value))
                hasCjk = true;
        }

        if (sb.Length == 0 || onlyNumbers)
            return null;
        if (count < 2 && !hasCjk)
            return null;
        return sb.ToString();
    }

    /// <summary>The name the game gives an unnamed tutorial character: <c>$</c> then letters and digits.</summary>
    public static bool IsPlaceholder(string raw) =>
        raw.Length >= 5 && raw[0] == '$' && raw.Skip(1).All(char.IsAsciiLetterOrDigit);

    public static bool IsCjk(int cp) =>
        (cp >= 0x4E00 && cp <= 0x9FFF)
        || (cp >= 0xAC00 && cp <= 0xD7AF)
        || (cp >= 0x3400 && cp <= 0x4DBF)
        || (cp >= 0x20000 && cp <= 0x2A6DF)
        || (cp >= 0x1100 && cp <= 0x11FF);

    /// <summary>Reads a scanned name candidate: valid UTF-8, starting with a letter or digit, sanitized to 2+ chars.</summary>
    public static string? ScanCandidate(ReadOnlySpan<byte> bytes)
    {
        var s = DecodeUtf8(bytes);
        if (string.IsNullOrEmpty(s) || !char.IsLetterOrDigit(s[0]))
            return null;
        var sanitized = Sanitize(s);
        return sanitized is { Length: >= 2 } ? sanitized : null;
    }
}
