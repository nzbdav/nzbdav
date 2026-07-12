using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Queue.FileProcessors;

// Altmount-style lazy RAR processor: parses only the first volume at import
// to learn the inner file name + size, and defers parsing of trailing
// volumes to first read (see LazyRarResolver). Returns null whenever the
// archive doesn't match the supported shape — caller falls back to the
// per-part eager RarProcessor in that case.
public class LazyRarProcessor(
    List<GetFileInfosStep.FileInfo> fileInfos,
    INntpClient usenetClient,
    string? password,
    CancellationToken ct
) : BaseProcessor
{
    // Conservative upper bound for the RAR continuation header at the start
    // of trailing volumes. Real values are typically 30-70 bytes. Wrong
    // estimates only affect seek targeting before resolution; the lazy
    // resolver overwrites with exact ranges on first read.
    private const int ContinuationHeaderGuess = 80;

    private const double YencDecodeRatio = 0.95;

    // We dropped the explicit "must be a single-file archive" check because
    // detecting it required walking past the first file header — which is
    // the slow seek that dominates first-volume parse cost. This sanity
    // check replaces it: if the matched file isn't large enough to span
    // most of the NZB, it's likely a companion file (.nfo, sample, etc.)
    // sitting in front of the actual video inside a multi-file archive,
    // so we bail out to let the eager processor pick the right one.
    private const double InnerFileSizeRatioThreshold = 0.70;

    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        var sorted = SortByFilename(fileInfos);
        if (sorted is null || sorted.Count == 0) return null;

        var firstInfo = sorted[0];
        var firstFileSize = firstInfo.FileSize
            ?? await usenetClient.GetFileSizeAsync(firstInfo.NzbFile, ct).ConfigureAwait(false);

        List<IRarHeader> headers;
        try
        {
            await using var firstStream = usenetClient.GetFileStream(
                firstInfo.NzbFile, firstFileSize, articleBufferSize: 0);
            // Stop as soon as the first file header lands. Walking further
            // forces SharpCompress to seek past the file data, which on
            // NzbFileStream triggers InterpolationSearch (~7 STAT calls)
            // and was the dominant cost of first-volume parse.
            headers = await RarUtil.ReadHeadersUntilFirstFileAsync(firstStream, password, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Information(
                "LazyRarProcessor: first-volume parse failed for {File}, falling back to eager: {Msg}",
                firstInfo.FileName, e.Message);
            return null;
        }

        var archiveHeader = headers.FirstOrDefault(h => h.HeaderType == HeaderType.Archive);
        if (archiveHeader is null)
        {
            Log.Information("LazyRarProcessor: no archive header in {File}, falling back to eager",
                firstInfo.FileName);
            return null;
        }
        if (!archiveHeader.GetIsFirstVolume())
        {
            Log.Information("LazyRarProcessor: {File} is not the first volume, falling back to eager",
                firstInfo.FileName);
            return null;
        }

        var fileHeader = headers.LastOrDefault(h => h.HeaderType == HeaderType.File && !h.IsDirectory());
        if (fileHeader is null)
        {
            Log.Information("LazyRarProcessor: no file header in {File}, falling back to eager",
                firstInfo.FileName);
            return null;
        }
        if (fileHeader.GetCompressionMethod() != 0)
        {
            Log.Information("LazyRarProcessor: {File} uses compression, falling back to eager",
                firstInfo.FileName);
            return null;
        }
        if (fileHeader.GetIsSolid())
        {
            Log.Information("LazyRarProcessor: {File} is solid, falling back to eager", firstInfo.FileName);
            return null;
        }

        var pathInArchive = fileHeader.GetFileName();
        var aesParams = fileHeader.GetAesParams(password);
        var totalFileSize = aesParams?.DecodedSize ?? fileHeader.GetUncompressedSize();

        // Inner-file-vs-NZB-size sanity check (replaces the dropped
        // multi-file count check). Only compares when we have PAR2 sizes
        // for every part; otherwise the comparison is unreliable.
        if (sorted.All(x => x.FileSize.HasValue))
        {
            var totalNzbSize = sorted.Sum(x => x.FileSize!.Value);
            if (totalNzbSize > 0 && totalFileSize < totalNzbSize * InnerFileSizeRatioThreshold)
            {
                Log.Information(
                    "LazyRarProcessor: {File} inner file {Inner} bytes is <{Ratio:P0} of NZB {Total}, " +
                    "likely a companion file in a multi-file archive — falling back to eager",
                    firstInfo.FileName, totalFileSize, InnerFileSizeRatioThreshold, totalNzbSize);
                return null;
            }
        }

        var firstPartByteRange = LongRange.FromStartAndSize(
            fileHeader.GetDataStartPosition(),
            fileHeader.GetAdditionalDataSize()
        );

        var firstPart = new DavMultipartFile.FilePart
        {
            SegmentIds = firstInfo.NzbFile.GetSegmentIds(),
            SegmentIdByteRange = LongRange.FromStartAndSize(0, firstFileSize),
            FilePartByteRange = firstPartByteRange,
        };

        var trailingInfos = sorted.Skip(1).ToList();
        var partSizes = trailingInfos
            .Select(pi => pi.FileSize ?? (long)(pi.NzbFile.GetTotalYencodedSize() * YencDecodeRatio))
            .ToArray();

        var pending = new List<DavMultipartFile.PendingPart>(trailingInfos.Count);
        var pendingSum = 0L;
        for (var i = 0; i < trailingInfos.Count; i++)
        {
            var partInfo = trailingInfos[i];
            var partSize = partSizes[i];
            var estimate = Math.Max(0, partSize - ContinuationHeaderGuess);
            pending.Add(new DavMultipartFile.PendingPart
            {
                SegmentIds = partInfo.NzbFile.GetSegmentIds(),
                SegmentIdByteRange = LongRange.FromStartAndSize(0, partSize),
                EstimatedDataSize = estimate,
            });
            pendingSum += estimate;
        }

        // Force the sum of estimates to match the inner file size exactly by
        // adjusting the last pending part. This keeps SeekFilePart aligned at
        // the file-end boundary even before any lazy resolution happens.
        if (pending.Count > 0)
        {
            var expectedPendingSum = totalFileSize - firstPartByteRange.Count;
            var adjustment = expectedPendingSum - pendingSum;
            var last = pending[^1];
            var adjusted = last.EstimatedDataSize + adjustment;
            if (adjusted < 0)
            {
                Log.Information(
                    "LazyRarProcessor: estimate overshoot for {File} ({Adjusted} bytes), falling back to eager",
                    firstInfo.FileName, adjusted);
                return null;
            }
            last.EstimatedDataSize = adjusted;
        }

        return new Result
        {
            PathInArchive = pathInArchive,
            TotalFileSize = totalFileSize,
            Password = password,
            AesParams = aesParams,
            FirstPart = firstPart,
            PendingParts = pending.ToArray(),
            ReleaseDate = firstInfo.ReleaseDate,
            ArchiveName = GetArchiveName(firstInfo.FileName),
        };
    }

    private static string GetArchiveName(string firstFileName)
    {
        var sansExtension = Path.GetFileNameWithoutExtension(firstFileName);
        return Regex.Replace(sansExtension, @"\.part\d+$", "", RegexOptions.IgnoreCase);
    }

    // Returns null if filenames don't all match the same RAR naming
    // convention (mixed schemes are too unusual to support lazily; eager
    // path handles them via the existing header-derived part numbers).
    private static List<GetFileInfosStep.FileInfo>? SortByFilename(List<GetFileInfosStep.FileInfo> infos)
    {
        var ranks = infos.Select(x => (Info: x, Part: ParsePartNumberFromFilename(x.FileName))).ToList();
        if (ranks.Any(r => r.Part is null)) return null;
        if (ranks.Select(r => r.Part!.Value).Distinct().Count() != ranks.Count) return null;
        return ranks.OrderBy(r => r.Part!.Value).Select(r => r.Info).ToList();
    }

    // .partXX.rar -> XX, .rXX -> XX + 100, .rar (legacy) -> 0.
    // The +100 keeps .r00..rNN strictly after .rar in mixed-scheme groups
    // (which we reject above, but the offset is harmless for pure-scheme groups).
    private static int? ParsePartNumberFromFilename(string filename)
    {
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success) return int.Parse(partMatch.Groups[1].Value);

        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success) return int.Parse(rMatch.Groups[1].Value) + 100;

        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            return 0;

        return null;
    }

    public new class Result : BaseProcessor.Result
    {
        public required string PathInArchive { get; init; }
        public required long TotalFileSize { get; init; }
        public required string? Password { get; init; }
        public required AesParams? AesParams { get; init; }
        public required DavMultipartFile.FilePart FirstPart { get; init; }
        public required DavMultipartFile.PendingPart[] PendingParts { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public required string ArchiveName { get; init; }
    }
}
