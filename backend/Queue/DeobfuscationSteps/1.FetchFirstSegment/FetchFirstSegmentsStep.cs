// ReSharper disable InconsistentNaming

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    public static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegments
    (
        List<NzbFile> nzbFiles,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null
    )
    {
        var files = nzbFiles.Where(x => x.Segments.Count > 0).ToList();

        if (configManager.IsPipeliningEnabled())
            return await FetchFirstSegmentsPipelined(
                files, usenetClient, configManager, cancellationToken, progress).ConfigureAwait(false);

        return await files
            .Select(x => FetchFirstSegment(x, usenetClient, cancellationToken))
            .WithConcurrencyAsync(Math.Min(configManager.GetMaxQueueConnections() + 5, 50))
            .GetAllAsync(cancellationToken, progress).ConfigureAwait(false);
    }

    private static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegmentsPipelined
    (
        List<NzbFile> files,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress
    )
    {
        var depth = configManager.GetPipeliningDepth();
        var segmentIds = files.Select(x => x.Segments[0].MessageId).ToList();
        var results = new NzbFileWithFirstSegment?[files.Count];
        var completed = 0;
        var index = 0;

        try
        {
            await foreach (var article in usenetClient.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var i = index++;
                if (article.Found && article.Stream != null)
                {
                    results[i] = await BuildFirstSegment(files[i], article.Stream, article.ArticleHeaders, cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(++completed);
                }
            }
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug($"Pipelined first-segment fetch aborted early ({e.Message}); " +
                      "falling back to per-article failover for the remainder.");
        }

        var pending = Enumerable.Range(0, files.Count).Where(i => results[i] is null).ToList();
        if (pending.Count > 0)
        {
            var rescued = await pending
                .Select(i => RescueFirstSegment(i, files[i], usenetClient, cancellationToken))
                .WithConcurrencyAsync(Math.Min(configManager.GetMaxQueueConnections() + 5, 50))
                .GetAllAsync(cancellationToken).ConfigureAwait(false);
            foreach (var (i, result) in rescued)
            {
                results[i] = result;
                progress?.Report(++completed);
            }
        }

        return results.Select(x => x!).ToList();
    }

    private static async Task<(int index, NzbFileWithFirstSegment result)> RescueFirstSegment
    (
        int index,
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return (index, await FetchFirstSegment(nzbFile, usenetClient, cancellationToken).ConfigureAwait(false));
        }
        catch (UsenetArticleNotFoundException)
        {
            Log.Warning("First segment for `{FileName}` missing across all providers",
                nzbFile.GetSubjectFileName());
            return (index, BuildMissingFirstSegment(nzbFile));
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            // Transient provider errors must not be treated as permanent missing segments
            // (otherwise fail-fast would mark good NZBs failed — see nzbdav-dev#245).
            Log.Warning(e,
                "First segment for `{FileName}` unavailable due to provider error; not treating as permanently missing",
                nzbFile.GetSubjectFileName());
            throw;
        }
    }

    private static NzbFileWithFirstSegment BuildMissingFirstSegment(NzbFile nzbFile) => new()
    {
        NzbFile = nzbFile,
        First16KB = null,
        Header = null,
        MissingFirstSegment = true,
        ReleaseDate = DateTimeOffset.UtcNow,
    };

    private static async Task<NzbFileWithFirstSegment> BuildFirstSegment
    (
        NzbFile nzbFile,
        YencStream bodyStream,
        UsenetArticleHeader? articleHeaders,
        CancellationToken cancellationToken
    )
    {
        await using var stream = bodyStream;
        var totalRead = 0;
        var buffer = new byte[16 * 1024];
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        var first16KB = totalRead < buffer.Length ? buffer.AsSpan(0, totalRead).ToArray() : buffer;
        var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);

        return new NzbFileWithFirstSegment
        {
            NzbFile = nzbFile,
            First16KB = first16KB,
            Header = yencHeaders,
            MissingFirstSegment = false,
            ReleaseDate = articleHeaders?.Date ?? DateTimeOffset.UtcNow,
        };
    }

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegment
    (
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // get the first article stream
            var firstSegment = nzbFile.Segments[0].MessageId;
            var article = await usenetClient.DecodedArticleAsync(firstSegment, cancellationToken).ConfigureAwait(false);
            await using var bodyStream = article.Stream!;

            // read up to the first 16KB from the stream
            var totalRead = 0;
            var buffer = new byte[16 * 1024];
            while (totalRead < buffer.Length)
            {
                var read = await bodyStream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            // determine bytes read
            var first16KB = totalRead < buffer.Length
                ? buffer.AsSpan(0, totalRead).ToArray()
                : buffer;

            // get the yencHeaders
            var yencHeaders = await bodyStream
                .GetYencHeadersAsync(cancellationToken)
                .ConfigureAwait(false);
            if (yencHeaders is not null)
                nzbFile.Segments[0].ByteRange =
                    LongRange.FromStartAndSize(yencHeaders.PartOffset, yencHeaders.PartSize);

            // return
            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = first16KB,
                Header = yencHeaders,
                MissingFirstSegment = false,
                ReleaseDate = article.ArticleHeaders!.Date
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return BuildMissingFirstSegment(nzbFile);
        }
    }

    public class NzbFileWithFirstSegment
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] Rar5Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

        public required NzbFile NzbFile { get; init; }
        public required UsenetYencHeader? Header { get; init; }
        public required byte[]? First16KB { get; init; }
        public required bool MissingFirstSegment { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public bool HasRar4Magic() => HasMagic(Rar4Magic);
        public bool HasRar5Magic() => HasMagic(Rar5Magic);

        private bool HasMagic(byte[] sequence)
        {
            return First16KB?.Length >= sequence.Length &&
                   First16KB.AsSpan(0, sequence.Length).SequenceEqual(sequence);
        }
    }
}
