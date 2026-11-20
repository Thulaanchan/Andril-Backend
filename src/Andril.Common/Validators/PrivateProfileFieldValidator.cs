using System.Globalization;
using System.Text;

namespace Andril.Common.Validators;

public static class PrivateProfileFieldValidator
{
    public const int MinimumDisplayNameLength = 3;
    public const int MaximumDisplayNameLength = 30;
    public const int MaximumBioLength = 300;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "andril", "admin", "administrator", "moderator", "support", "system", "official",
        "andriladmin", "andrilmoderator", "andrilsupport", "andrilofficial", "andrilteam"
    };

    public static bool TryNormalizeDisplayName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || ContainsInvalidUnicode(value)) return false;

        var trimmed = value.Trim();
        if (new StringInfo(trimmed).LengthInTextElements is < MinimumDisplayNameLength or > MaximumDisplayNameLength
            || !HasAllowedNameCharacters(trimmed) || IsReservedDisplayName(trimmed)) return false;

        normalized = trimmed;
        return true;
    }

    public static bool IsValidBio(string? value) => value is null
        || (!ContainsInvalidUnicode(value) && new StringInfo(value).LengthInTextElements <= MaximumBioLength);

    private static bool ContainsInvalidUnicode(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '\0' || char.IsLowSurrogate(character)) return true;
            if (char.IsHighSurrogate(character)
                && (++index == value.Length || !char.IsLowSurrogate(value[index]))) return true;
        }
        return false;
    }

    private static bool HasAllowedNameCharacters(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        bool hasLetterOrDigit = false;
        bool hasMarkBase = false;
        for (int index = 0; index < runes.Length; index++)
        {
            var rune = runes[index];
            if (rune.Value is 0x200C or 0x200D)
            {
                if (!IsScriptJoiner(runes, index)) return false;
                continue;
            }

            // Default-ignorable marks and fillers, the keycap mark, and the information emoji.
            if (rune.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5
                or >= 0x180B and <= 0x180D or 0x180F or 0x20E3 or 0x2139 or 0x3164 or 0xFFA0
                or >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF) return false;

            if (Rune.IsLetterOrDigit(rune))
                hasLetterOrDigit = hasMarkBase = true;
            else if (rune.Value is ' ' or '_' or '-')
                hasMarkBase = false;
            else if (!hasMarkBase || Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                return false;
        }
        return hasLetterOrDigit;
    }

    private static bool IsScriptJoiner(Rune[] runes, int index)
    {
        if (index < 1 || index + 1 >= runes.Length) return false;

        // Tamil pulli and Sinhala al-lakuna may use join controls between consonants.
        if (index >= 2 && runes[index - 1].Value is 0x0BCD or 0x0DCA)
        {
            int virama = runes[index - 1].Value;
            return IsConsonant(runes[index - 2], virama) && IsConsonant(runes[index + 1], virama);
        }

        // Sinhala also encodes touching conjuncts with ZWJ before the al-lakuna.
        return runes[index].Value == 0x200D && index + 2 < runes.Length && runes[index + 1].Value == 0x0DCA
            && IsConsonant(runes[index - 1], 0x0DCA) && IsConsonant(runes[index + 2], 0x0DCA);
    }

    private static bool IsConsonant(Rune rune, int virama) => Rune.IsLetter(rune)
        && (virama == 0x0BCD ? rune.Value is >= 0x0B95 and <= 0x0BB9 : rune.Value is >= 0x0D9A and <= 0x0DC6);

    private static bool IsReservedDisplayName(string value)
    {
        var key = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is ' ' or '_' or '-') continue;
            key.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        }
        return ReservedNames.Contains(key.ToString());
    }
}
