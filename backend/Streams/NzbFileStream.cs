using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize,
    LongRange[]? segmentByteRanges = null,
    bool usePipelinedBodyRequests = true
) : FastReadOnlyStream
{
    private const long MaximumForwardDrainBytes = 1024 * 1024;
    private long _position;
    private long _pendingForwardDrain;
    private bool _disposed;
    private Stream? _innerStream;
    private readonly LongRange[]? _segmentByteRanges =
        AreSegmentByteRangesValid(segmentByteRanges, fileSegmentIds.Length, fileSize)
            ? segmentByteRanges
            : null;

    // Average yEnc-decoded size per segment in this file. Used to (a) zero-fill
    // missing segments mid-stream so the demuxer can resync instead of the
    // player closing on a truncated body, and (b) synthesize a probe range
    // when SeekSegment can't fetch a missing segment's yEnc header. yEnc
    // segments within a single NzbFile are produced uniformly except for the
    // tail, so the average is within a few percent of any real segment.
    private long ExpectedSegmentSize =>
        fileSegmentIds.Length > 0 ? Math.Max(1, fileSize / fileSegmentIds.Length) : 0;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        if (_position >= fileSize) return 0;
        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        if (_pendingForwardDrain > 0)
        {
            try
            {
                await _innerStream.DiscardBytesAsync(
                    _pendingForwardDrain, cancellationToken).ConfigureAwait(false);
                _pendingForwardDrain = 0;
            }
            catch
            {
                await _innerStream.DisposeAsync().ConfigureAwait(false);
                _innerStream = null;
                _pendingForwardDrain = 0;
                throw;
            }
        }

        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long absoluteOffset;
        try
        {
            absoluteOffset = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(fileSize + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Invalid seek origin.")
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");
        }

        if (absoluteOffset < 0 || absoluteOffset > fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Seek position is outside stream bounds.");

        if (_position == absoluteOffset) return _position;
        if (_innerStream is not null &&
            absoluteOffset > _position &&
            absoluteOffset - _position <= MaximumForwardDrainBytes)
        {
            _pendingForwardDrain += absoluteOffset - _position;
            _position = absoluteOffset;
            return _position;
        }

        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        _pendingForwardDrain = 0;
        return _position;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        if (_segmentByteRanges is not null)
        {
            return InterpolationSearch.Find(
                byteOffset,
                new LongRange(0, _segmentByteRanges.Length),
                new LongRange(0, fileSize),
                guess => _segmentByteRanges[guess]
            );
        }

        var avg = ExpectedSegmentSize;
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                try
                {
                    var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                    return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
                }
                catch (UsenetArticleNotFoundException e)
                {
                    // The probe segment itself is missing — fall back to a
                    // synthetic uniform-size range so interpolation can still
                    // converge. The actual body read of this segment (if it
                    // turns out to be the seek target) gets zero-filled by
                    // MultiSegmentStream.
                    Log.Warning(
                        "Seek probe hit missing article {SegmentId} (segment index {Index}). Using estimated range.",
                        e.SegmentId, guess);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
                catch (Exception e) when (articleBufferSize > 0 && !ct.IsCancellationRequested)
                {
                    Log.Warning(e,
                        "Seek probe transient failure on segment index {Index}. Using estimated range.", guess);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
            },
            ct
        ).ConfigureAwait(false);
    }

    private static bool AreSegmentByteRangesValid(LongRange[]? ranges, int segmentCount, long expectedFileSize)
    {
        if (ranges is null || ranges.Length != segmentCount || ranges.Length == 0) return false;
        if (ranges[0].StartInclusive != 0 || ranges[^1].EndExclusive != expectedFileSize) return false;

        for (var i = 0; i < ranges.Length; i++)
        {
            if (ranges[i].Count <= 0) return false;
            if (i > 0 && ranges[i - 1].EndExclusive != ranges[i].StartInclusive) return false;
        }

        return true;
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, failFastOnFirstSegment: true, cancellationToken);
        var fast = await TryGetSeekStreamFast(rangeStart, cancellationToken).ConfigureAwait(false);
        if (fast != null) return fast;

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, failFastOnFirstSegment: false, cancellationToken);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, cancellationToken)
            .ConfigureAwait(false);
        return stream;
    }

    private const int MaxSeekGuessCorrection = 3;

    private async Task<Stream?> TryGetSeekStreamFast(long rangeStart, CancellationToken ct)
    {
        var avg = ExpectedSegmentSize;
        if (avg <= 0 || fileSegmentIds.Length == 0) return null;

        var index = (int)Math.Clamp(rangeStart / avg, 0, fileSegmentIds.Length - 1);

        for (var step = 0; step <= MaxSeekGuessCorrection; step++)
        {
            UsenetDecodedBodyResponse response;
            try
            {
                response = await usenetClient.DecodedBodyAsync(fileSegmentIds[index], ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            var body = response.Stream;
            UsenetYencHeader? header;
            try
            {
                header = await body.GetYencHeadersAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            if (header == null)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            var start = header.PartOffset;
            var end = header.PartOffset + header.PartSize;

            if (rangeStart < start || rangeStart >= end)
            {
                await body.DisposeAsync().ConfigureAwait(false);
                var next = rangeStart < start ? index - 1 : index + 1;
                if (next < 0 || next >= fileSegmentIds.Length) return null;
                index = next;
                continue;
            }

            MemoryStream head;
            try
            {
                await body.DiscardBytesAsync(rangeStart - start, ct).ConfigureAwait(false);
                var tail = end - rangeStart;
                var capacity = tail is > 0 and <= int.MaxValue ? (int)tail : 0;
                head = new MemoryStream(capacity);
                await body.CopyToAsync(head, ct).ConfigureAwait(false);
                head.Position = 0;
            }
            finally
            {
                await body.DisposeAsync().ConfigureAwait(false);
            }

            return new CombinedStream(SpliceHeadThenRest(head, index + 1, ct));
        }

        return null;
    }

    private IEnumerable<Task<Stream>> SpliceHeadThenRest(Stream head, int restFirstIndex, CancellationToken ct)
    {
        yield return Task.FromResult(head);
        yield return Task.FromResult(GetMultiSegmentStream(restFirstIndex, failFastOnFirstSegment: false, ct));
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, bool failFastOnFirstSegment,
        CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(
            segmentIds,
            usenetClient,
            articleBufferSize,
            ExpectedSegmentSize,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing) _innerStream?.Dispose();
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
