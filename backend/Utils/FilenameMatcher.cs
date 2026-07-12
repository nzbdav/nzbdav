using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

public static class FilenameMatcher
{
    private static readonly Regex BoundaryRegex = new(
        @"\b(\d{4}|S\d{1,2}(E\d{1,3})?|\d{3,4}p)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAlnumRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled);

    private static readonly Dictionary<char, string> LatinFolding = new()
    {
        ['ø'] = "o",
        ['œ'] = "oe",
        ['æ'] = "ae",
        ['ł'] = "l",
        ['đ'] = "d",
        ['ð'] = "d",
        ['þ'] = "th",
        ['ß'] = "ss",
        ['ı'] = "i",
        ['ŋ'] = "n",
    };

    private static string Fold(string s)
    {
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            if (LatinFolding.TryGetValue(c, out var rep))
                sb.Append(rep);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static string[] HeadTokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var lower = Fold(s);
        var m = BoundaryRegex.Match(lower);
        while (m.Success && m.Index == 0) m = m.NextMatch();
        var head = m.Success ? lower[..m.Index] : lower;
        return NonAlnumRegex.Replace(head, " ")
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool TokensEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public static bool Matches(string? query, string? candidate)
    {
        var q = HeadTokens(query);
        if (q.Length == 0) return true;
        return TokensEqual(q, HeadTokens(candidate));
    }

    private static readonly Regex SeasonEpisodeRegex = new(
        @"\bs(\d{1,2})[. _-]?e(\d{1,3})(?:(?:-|e)e?(\d{1,3}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AltEpisodeRegex = new(
        @"(?<![a-z0-9])(\d{1,2})x(\d{1,3})(?:[-x](\d{1,3}))?(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonWordRegex = new(
        @"\b(?:season|series)[. _]*(\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeasonTokenRegex = new(
        @"\bs(\d{1,2})(?![. _-]?e?\d)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public readonly record struct EpisodeTag(int Season, int? Episode, int? EpisodeEnd);

    public static EpisodeTag? ParseEpisode(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var m = SeasonEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);

        m = AltEpisodeRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null);

        m = SeasonWordRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(int.Parse(m.Groups[1].Value), null, null);

        m = SeasonTokenRegex.Match(title);
        if (m.Success)
            return new EpisodeTag(int.Parse(m.Groups[1].Value), null, null);

        return null;
    }

    public static bool EpisodeCompatible(string? title, int? season, int? episode)
    {
        if (ParseEpisode(title) is not { } tag) return true;
        if (season is { } s && tag.Season != s) return false;
        if (episode is { } e && tag.Episode is { } te)
        {
            var end = tag.EpisodeEnd ?? te;
            if (e < te || e > end) return false;
        }
        return true;
    }

    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.Ordinal) { "the", "a", "an" };

    private static readonly HashSet<string> TrailingQualifiers =
        new(StringComparer.Ordinal) { "us", "uk", "au", "ca", "nz", "ie", "za" };

    private static string[] StripLeadingArticle(string[] tokens) =>
        tokens.Length > 1 && LeadingArticles.Contains(tokens[0]) ? tokens[1..] : tokens;

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var tokens = NonAlnumRegex.Replace(Fold(title), " ")
                                  .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', StripLeadingArticle(tokens));
    }

    public static bool TitleMatches(IReadOnlyCollection<string> expectedNormalized, string? releaseTitle)
    {
        if (expectedNormalized.Count == 0) return true;
        var head = StripLeadingArticle(HeadTokens(releaseTitle));
        if (head.Length == 0) return false;

        if (expectedNormalized.Contains(string.Join(' ', head))) return true;

        if (head.Length >= 2)
        {
            var last = head[^1];
            if (TrailingQualifiers.Contains(last) || (last.Length == 4 && last.All(char.IsDigit)))
                return expectedNormalized.Contains(string.Join(' ', head[..^1]));
        }
        return false;
    }
}
