using System;
using System.Globalization;
using System.Text;

namespace Peel;

/// <summary>Bir tanımlayıcının gizlenmiş (anlamsız) olup olmadığı. Türkçe, Kiril, Yunan gibi alfabelerle yazılmış gerçek
/// adlar korunur; görünmez/biçim karakterleri, özel kullanım alanı, CJK gürültüsü ve "#=" önekli (Eazfuscator) adlar gizlenmiş sayılır.</summary>
public static class Names
{
    public static bool IsObfuscated(string? name, bool shortNamesToo = false)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith("#=", StringComparison.Ordinal)) return true;
        if (Passes.GuidName.IsMatch(name)) return true;
        foreach (var r in name.EnumerateRunes())
        {
            int v = r.Value;
            if (v < 0x80)
            {
                if (char.IsAsciiLetterOrDigit((char)v) || "_<>`.$@?-=|{}".Contains((char)v)) continue;
                return true;
            }
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.Format or UnicodeCategory.Control or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.Surrogate or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return true;
            if (!Rune.IsLetterOrDigit(r)) return true;
            // Latin genişletilmiş, Yunan, Kiril dışındaki alfabeler (CJK, Hangul ...) gizleyicilerin sık kullandığı gürültü
            if (v > 0x052F) return true;
        }
        if (shortNamesToo && name.Length <= 2 && !name.StartsWith('<')) return true;
        return false;
    }

    /// <summary>Raporda ve günlükte gösterim: görünmez karakterler \uXXXX olarak.</summary>
    public static string Show(string? name)
    {
        if (name == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var r in name.EnumerateRunes())
        {
            if (r.Value < 0x80 && r.Value >= 0x20) sb.Append((char)r.Value);
            else if (Rune.IsLetterOrDigit(r) && r.Value <= 0x052F) sb.Append(r.ToString());
            else sb.Append($"\\u{r.Value:X4}");
            if (sb.Length > 60) { sb.Append('…'); break; }
        }
        return sb.ToString();
    }
}
