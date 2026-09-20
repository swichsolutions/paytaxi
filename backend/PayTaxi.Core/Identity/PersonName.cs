using System.Globalization;
using System.Text;

namespace PayTaxi.Core.Identity;

/// <summary>
/// Script-blind comparison of two person names. Parks register drivers in whatever script
/// the Yandex console or their own records use (Georgian, Latin, occasionally Russian), and a
/// driver types the bank account holder name as his ID spells it. Both must count as the same
/// person: "გიორგი მამულაშვილი", "Giorgi Mamulashvili" and "Гиорги Мамулашвили" all match.
///
/// Method: split into words, transliterate each word to lowercase Latin letters, drop anything
/// that is not a–z, then require every word of the shorter name to appear in the longer one
/// (so an added or omitted middle name still matches, word order is irrelevant).
///
/// This is deliberately lenient. It exists to catch a different PERSON (a wife's or friend's
/// name typed against the driver's account), not to be a precise transliteration engine —
/// several Georgian letters collapse to one Latin letter (თ/ტ → t) by design, and after
/// transliteration common Latin spelling variants are folded too (ღვინიაშვილი is written
/// "Gviniashvili" far more often than "Ghviniashvili"; ყიფიანი as "Kipiani", not "Qipiani").
/// Over-merging two genuinely different people is practically impossible on full names,
/// while a false refusal blocks a real driver — so every fold errs toward matching.
/// </summary>
public static class PersonName
{
    private static readonly Dictionary<char, string> Georgian = new()
    {
        ['ა'] = "a", ['ბ'] = "b", ['გ'] = "g", ['დ'] = "d", ['ე'] = "e", ['ვ'] = "v", ['ზ'] = "z",
        ['თ'] = "t", ['ი'] = "i", ['კ'] = "k", ['ლ'] = "l", ['მ'] = "m", ['ნ'] = "n", ['ო'] = "o",
        ['პ'] = "p", ['ჟ'] = "zh", ['რ'] = "r", ['ს'] = "s", ['ტ'] = "t", ['უ'] = "u", ['ფ'] = "p",
        ['ქ'] = "k", ['ღ'] = "gh", ['ყ'] = "q", ['შ'] = "sh", ['ჩ'] = "ch", ['ც'] = "ts", ['ძ'] = "dz",
        ['წ'] = "ts", ['ჭ'] = "ch", ['ხ'] = "kh", ['ჯ'] = "j", ['ჰ'] = "h",
    };

    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "",
        ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>
    /// True when the two names plausibly denote the same person, regardless of script, word
    /// order, or an extra/missing middle name. False when either side yields no usable words.
    /// </summary>
    public static bool LooksLikeSamePerson(string? a, string? b)
    {
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return false;

        var (shorter, longer) = wa.Count <= wb.Count ? (wa, wb) : (wb, wa);
        return shorter.All(longer.Contains);
    }

    /// <summary>Canonical lowercase-Latin words of a name, for matching. Words under 2 letters are dropped (initials, noise).</summary>
    public static HashSet<string> Words(string? name)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(name)) return set;

        foreach (var raw in name.Split(new[] { ' ', '-', '\t', '\n', ',', '.', '\'' , '’' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Transliterate(raw);
            if (w.Length >= 2) set.Add(w);
        }
        return set;
    }

    /// <summary>Lowercase Latin a–z only, folded (see <see cref="Fold"/>). Georgian and Cyrillic are transliterated; Latin diacritics are stripped; everything else is dropped.</summary>
    public static string Transliterate(string word)
    {
        var sb = new StringBuilder(word.Length * 2);
        foreach (var ch in word.Normalize(NormalizationForm.FormD))
        {
            if (Georgian.TryGetValue(ch, out var g)) { sb.Append(g); continue; }
            var lower = char.ToLowerInvariant(ch);
            if (Cyrillic.TryGetValue(lower, out var c)) { sb.Append(c); continue; }
            if (lower is >= 'a' and <= 'z') { sb.Append(lower); continue; }
            // Combining marks (from FormD) and any other symbol are dropped.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
        }
        return Fold(sb.ToString());
    }

    private static readonly (string From, string To)[] Folds =
    {
        ("ph", "p"), ("th", "t"), ("gh", "g"), ("kh", "h"), ("zh", "j"), ("dz", "z"),
        ("ts", "c"), ("tz", "c"), ("ch", "c"), ("sh", "s"),
        ("q", "k"), ("y", "i"), ("w", "v"), ("f", "p"), ("x", "ks"),
    };

    /// <summary>
    /// Collapse Latin spelling variants that denote the same Georgian sound, then drop doubled
    /// letters. Applied to BOTH names, so the exact target letters do not matter — only that
    /// the same sound always lands on the same letter.
    /// </summary>
    public static string Fold(string latin)
    {
        var s = latin;
        foreach (var (from, to) in Folds) s = s.Replace(from, to);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (sb.Length == 0 || sb[^1] != ch) sb.Append(ch);
        return sb.ToString();
    }
}
