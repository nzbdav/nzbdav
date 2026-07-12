using System.Text.RegularExpressions;

namespace NzbWebDAV.Models.Nzb;

public class NzbFile
{
    public required string Subject { get; init; }
    public List<NzbSegment> Segments { get; } = [];

    public string[] GetSegmentIds()
    {
        return Segments
            .Select(x => x.MessageId)
            .ToArray();
    }

    public LongRange[]? GetSegmentByteRanges()
    {
        var ranges = Segments
            .Select(x => x.ByteRange)
            .ToArray();

        if (ranges.Length == 0) return null;

        if (ranges.All(x => x is not null))
            return ValidateSegmentByteRanges(ranges.Select(x => x!).ToArray());

        var firstRange = ranges[0];
        var lastRange = ranges[^1];
        if (firstRange is null || lastRange is null ||
            firstRange.StartInclusive != 0 || firstRange.Count <= 0 || lastRange.Count <= 0)
            return null;

        try
        {
            var inferredRanges = Enumerable.Range(0, ranges.Length)
                .Select(index =>
                {
                    var start = checked(firstRange.Count * index);
                    var end = index == ranges.Length - 1
                        ? lastRange.EndExclusive
                        : checked(start + firstRange.Count);
                    return new LongRange(start, end);
                })
                .ToArray();

            if (inferredRanges[^1].StartInclusive != lastRange.StartInclusive) return null;

            for (var i = 0; i < ranges.Length; i++)
            {
                if (ranges[i] is { } knownRange &&
                    (knownRange.StartInclusive != inferredRanges[i].StartInclusive ||
                     knownRange.EndExclusive != inferredRanges[i].EndExclusive))
                    return null;
            }

            return ValidateSegmentByteRanges(inferredRanges);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static LongRange[]? ValidateSegmentByteRanges(LongRange[] ranges)
    {
        if (ranges[0].StartInclusive != 0) return null;

        for (var i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Count <= 0) return null;
            if (i > 0 && ranges[i - 1].EndExclusive != ranges[i].StartInclusive) return null;
        }

        return ranges;
    }

    public long GetTotalYencodedSize()
    {
        return Segments
            .Select(x => x.Bytes)
            .Sum();
    }

    public string GetSubjectFileName()
    {
        return GetFirstValidNonEmptyFilename(
            TryParseSubjectFilename1,
            TryParseSubjectFilename2
        );
    }

    private string TryParseSubjectFilename1()
    {
        // The most common format is when filename appears in double quotes
        // example: `[1/8] - "file.mkv" yEnc 12345 (1/54321)`
        var match = Regex.Match(Subject, "\\\"(.*)\\\"");
        return match.Success ? match.Groups[1].Value : "";
    }

    private string TryParseSubjectFilename2()
    {
        // Otherwise, use sabnzbd's regex
        // https://github.com/sabnzbd/sabnzbd/blob/b6b0d10367fd4960bad73edd1d3812cafa7fc002/sabnzbd/nzbstuff.py#L106
        var match = Regex.Match(Subject, @"\b([\w\-+()' .,]+(?:\[[\w\-\/+()' .,]*][\w\-+()' .,]*)*\.[A-Za-z0-9]{2,4})\b");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string GetFirstValidNonEmptyFilename(params Func<string>[] funcs)
    {
        return funcs
            .Select(x => x.Invoke())
            .Where(x => x == Path.GetFileName(x))
            .FirstOrDefault(x => x != "") ?? "";
    }
}
