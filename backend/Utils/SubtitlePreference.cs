using System.Globalization;
using System.IO;
using System.Text;

namespace NzbWebDAV.Utils;

/// <summary>
/// Helpers for biasing playback failover toward releases that carry subtitles.
/// Operates on the free-text subtitle attribute supplied by indexers (the Newznab
/// "subs" attribute) and on sidecar subtitle filenames found inside a release's NZB,
/// normalising indexer values into comparable language tokens so a fallback candidate
/// can be ranked by whether it shares a subtitle language with the release the user
/// actually clicked. Pure and side-effect free.
/// </summary>
public static class SubtitlePreference
{
    // Folds common language names and ISO-639 codes onto a single canonical token, so
    // "English" / "eng" / "en" coming from different indexers all compare equal. Tokens
    // not listed here fall back to their normalised raw form (so two indexers using the
    // same uncommon spelling still match).
    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.Ordinal)
    {
        ["english"] = "en",
        ["eng"] = "en",
        ["en"] = "en",
        ["spanish"] = "es",
        ["espanol"] = "es",
        ["castellano"] = "es",
        ["latino"] = "es",
        ["spa"] = "es",
        ["esp"] = "es",
        ["es"] = "es",
        ["french"] = "fr",
        ["francais"] = "fr",
        ["fra"] = "fr",
        ["fre"] = "fr",
        ["fr"] = "fr",
        ["german"] = "de",
        ["deutsch"] = "de",
        ["ger"] = "de",
        ["deu"] = "de",
        ["de"] = "de",
        ["italian"] = "it",
        ["italiano"] = "it",
        ["ita"] = "it",
        ["it"] = "it",
        ["portuguese"] = "pt",
        ["portugues"] = "pt",
        ["brazilian"] = "pt",
        ["por"] = "pt",
        ["pt"] = "pt",
        ["dutch"] = "nl",
        ["nederlands"] = "nl",
        ["nld"] = "nl",
        ["dut"] = "nl",
        ["nl"] = "nl",
        ["russian"] = "ru",
        ["rus"] = "ru",
        ["ru"] = "ru",
        ["japanese"] = "ja",
        ["jpn"] = "ja",
        ["jap"] = "ja",
        ["ja"] = "ja",
        ["jp"] = "ja",
        ["korean"] = "ko",
        ["kor"] = "ko",
        ["ko"] = "ko",
        ["chinese"] = "zh",
        ["mandarin"] = "zh",
        ["cantonese"] = "zh",
        ["zho"] = "zh",
        ["chi"] = "zh",
        ["zh"] = "zh",
        ["arabic"] = "ar",
        ["ara"] = "ar",
        ["ar"] = "ar",
        ["hindi"] = "hi",
        ["hin"] = "hi",
        ["hi"] = "hi",
        ["swedish"] = "swe",
        ["swe"] = "swe",
        ["sv"] = "swe",
        ["norwegian"] = "nor",
        ["nor"] = "nor",
        ["nb"] = "nor",
        ["danish"] = "dan",
        ["dan"] = "dan",
        ["da"] = "dan",
        ["finnish"] = "fin",
        ["fin"] = "fin",
        ["fi"] = "fin",
        ["polish"] = "pol",
        ["pol"] = "pol",
        ["pl"] = "pol",
        ["turkish"] = "tur",
        ["tur"] = "tur",
        ["tr"] = "tur",
    };

    private static readonly char[] Separators =
        [',', '/', ';', '|', '+', '&', ' ', '\t', '(', ')', '[', ']', '{', '}', '-', '_', '.'];

    // Sidecar subtitle file extensions, used to detect subtitles shipped alongside the
    // video inside a release's NZB file list.
    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt",
    };

    /// <summary>
    /// Parse an indexer subtitle attribute (e.g. "English, Spanish") into a set of canonical
    /// language tokens. Returns an empty set when no subtitles are declared.
    /// </summary>
    public static HashSet<string> ParseLanguages(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value)) return result;

        foreach (var raw in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = Fold(raw);
            if (token.Length == 0) continue;
            result.Add(LanguageAliases.TryGetValue(token, out var canonical) ? canonical : token);
        }
        return result;
    }

    /// <summary>
    /// Rank a fallback candidate by subtitle desirability relative to the clicked release:
    /// 2 = shares a subtitle language with the click, 1 = carries subtitles, 0 = none.
    /// Higher ranks are preferred during failover.
    /// </summary>
    public static int Rank(string? candidateSubs, IReadOnlySet<string> primaryLanguages, bool primaryHasSubs)
    {
        var languages = ParseLanguages(candidateSubs);
        if (languages.Count == 0) return 0;
        if (primaryHasSubs && languages.Overlaps(primaryLanguages)) return 2;
        return 1;
    }

    /// <summary>
    /// True when the file name is a sidecar subtitle file (.srt/.ass/.ssa/.sub/.idx/.vtt).
    /// </summary>
    public static bool IsSubtitleFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && SubtitleExtensions.Contains(ext);
    }

    // Lowercase, strip accents, keep only letters/digits — so "Português" folds to "portugues".
    private static string Fold(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }
}
