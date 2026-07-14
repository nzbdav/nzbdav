using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Requests;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

[ApiController]
[Route("view/{*path}")]
public class GetWebdavItemController(
    DatabaseStore store,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker,
    ActiveReadRegistry activeReadRegistry,
    CandidateNegativeCache negativeCache
) : ControllerBase
{
    private async Task<Stream> GetWebdavItem(GetWebdavItemRequest request)
    {
        var item = await store.GetItemAsync(request.Item, HttpContext.RequestAborted).ConfigureAwait(false);
        if (item is null) throw new BadHttpRequestException("The file does not exist.");
        if (item is IStoreCollection) throw new BadHttpRequestException("The file does not exist.");

        // disable compression to keep Content-Length intact for clients that need seeking
        Response.Headers["Content-Encoding"] = "identity";

        // handle par2 preview
        if (Path.GetExtension(item.Name).ToLower() == ".par2" && configManager.IsPreviewPar2FilesEnabled())
            return await GetPar2PreviewStream(item).ConfigureAwait(false);

        // Provisional budget for fully-specified ranges before stream creation.
        if (request.RangeStart is { } provisionalStart && request.RangeEnd is { } provisionalEnd)
            RangeContext.SetReadBudget(provisionalEnd - provisionalStart + 1);

        // get the file stream and set the file-size in header
        var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileSize = stream.Length;

        var idFile = item as DatabaseStoreIdFile;

        if (idFile?.HistoryItemId is { } hid)
            HttpContext.Items["historyItemId"] = hid;

        // Now that the real filename + size are known, update the active-read
        // entry so the UI shows the human-readable name instead of the .ids GUID.
        if (HttpContext.Items["readSessionId"] is Guid sid)
        {
            var displayName = idFile?.FriendlyName ?? item.Name;
            activeReadRegistry.UpdateInfo(sid, displayName, fileSize);
        }

        // set the content-type and content-disposition headers
        Response.Headers["Content-Type"] = GetContentType(item.Name);
        Response.Headers["Content-Disposition"] = GetContentDisposition(item.Name, request.ShouldDownload);

        // disable compression to keep Content-Length intact for clients that need seeking
        Response.Headers["Content-Encoding"] = "identity";
        Response.Headers["Accept-Ranges"] = "bytes";

        // Resolve the suffix form ("bytes=-N", last N bytes) now that fileSize
        // is known. Clamp at zero so an oversized suffix means "the whole file"
        // rather than seeking before byte 0.
        long? rangeStart = request.RangeStart;
        long? rangeEnd = request.RangeEnd;
        if (request.SuffixLength is { } suffixLen)
        {
            rangeStart = Math.Max(0, fileSize - suffixLen);
            rangeEnd = fileSize - 1;
        }

        // Stash the effective start so HandleRequest can report playback
        // position from the real offset (not from 0) for suffix-range reads.
        HttpContext.Items["effectiveRangeStart"] = rangeStart ?? 0L;

        if (rangeStart is not null)
        {
            var end = rangeEnd ?? (fileSize - 1);

            // Syntactically valid but unsatisfiable → 416 (mirror WebDAV handler).
            if (rangeStart.Value < 0 || rangeStart.Value >= fileSize || rangeStart.Value > end)
            {
                Response.Headers["Content-Range"] = $"bytes */{fileSize}";
                Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                await stream.DisposeAsync().ConfigureAwait(false);
                return Stream.Null;
            }

            var chunkSize = 1 + end - rangeStart.Value;

            // Cap prefetch at the range end before Seek recreates segment streams.
            RangeContext.SetReadBudget(chunkSize);

            // seek
            stream.Seek(rangeStart.Value, SeekOrigin.Begin);
            if (rangeEnd is not null) stream = stream.LimitLength(chunkSize);

            // set response headers
            Response.Headers["Content-Range"] = $"bytes {rangeStart}-{end}/{fileSize}";
            Response.Headers["Content-Length"] = chunkSize.ToString();
            Response.StatusCode = 206;
        }
        else
        {
            RangeContext.SetReadBudget(null);
            Response.Headers["Content-Length"] = fileSize.ToString();
        }

        return stream;
    }

    [HttpGet]
    public async Task HandleRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            var sessionId = TrackReadSession(request.Item);
            HttpContext.Items["readSessionId"] = sessionId;
            using var scope = providerUsageTracker.BeginScope(sessionId);
            using var metricsScope = MultiProviderNntpClient.BeginReadSessionScope(sessionId);
            await using var response = await GetWebdavItem(request);
            var effectiveStart = (long)(HttpContext.Items["effectiveRangeStart"] ?? 0L);
            await CopyAndReportAsync(response, Response.Body, sessionId, effectiveStart, HttpContext.RequestAborted);
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    private async Task CopyAndReportAsync(Stream src, Stream dest, Guid sessionId, long startOffset, CancellationToken ct)
    {
        // 64 KB chunks; after each write report (bytesRead, absolutePosition)
        // so the Right-Now panel can show real playback location and the
        // throughput rate populates correctly.
        var buffer = new byte[64 * 1024];
        var position = startOffset;
        while (true)
        {
            int read;
            try
            {
                read = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                if (HttpContext.Items["historyItemId"] is Guid hid)
                {
                    negativeCache.MarkHistoryItemBroken(hid);
                    Serilog.Log.Warning(
                        "Mid-read failed at offset {Offset} for HistoryItem {HistoryItemId}: {Message}",
                        position, hid, e.Message);
                    PoisonFileNameAsync(hid);
                }
                throw;
            }
            if (read <= 0) break;
            await dest.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
            position += read;
            activeReadRegistry.Touch(sessionId, read, position);
        }
    }

    private void PoisonFileNameAsync(Guid historyItemId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var ctx = new DavDatabaseContext();
                var fileName = await ctx.HistoryItems.AsNoTracking()
                    .Where(h => h.Id == historyItemId)
                    .Select(h => h.FileName)
                    .FirstOrDefaultAsync().ConfigureAwait(false);
                if (!string.IsNullOrEmpty(fileName))
                    negativeCache.MarkFileNameBroken(fileName);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "PoisonFileNameAsync for {HistoryItemId} failed", historyItemId);
            }
        });
    }

    private Guid TrackReadSession(string itemPath)
    {
        // Provisional name from the URL path. GetWebdavItem replaces it with
        // item.Name (the real human-readable filename) once the store lookup runs.
        var fileName = Path.GetFileName(itemPath);
        var clientKey = $"{HttpContext.Connection.RemoteIpAddress}|{Request.Headers.UserAgent}";
        return activeReadRegistry.GetOrCreate(itemPath, clientKey, fileName, fileSize: null);
    }

    [HttpHead]
    public async Task HandleHeadRequest()
    {
        try
        {
            HttpContext.Items["configManager"] = configManager;
            var request = new GetWebdavItemRequest(HttpContext);
            await using var response = await GetWebdavItem(request).ConfigureAwait(false);
            // HEAD: headers already set, body omitted
        }
        catch (UnauthorizedAccessException)
        {
            Response.StatusCode = 401;
        }
    }

    private static string GetContentType(string item)
    {
        if (item == "README") return "text/plain";
        var extension = Path.GetExtension(item).ToLower();
        // .mkv falls through to ContentTypeUtil → "video/x-matroska". The old
        // override returned "video/webm", but WebM only permits VP8/VP9 + Vorbis/Opus.
        // Releases using H.264/H.265 + AC3/DTS made strict players reject the
        // video stream while still decoding audio.
        return extension == ".rclonelink" ? "text/plain"
            : extension == ".nfo" ? "text/plain"
            : ContentTypeUtil.GetContentType(Path.GetFileName(item));
    }

    private static string GetContentDisposition(string filename, bool shouldDownload)
    {
        // Remove control characters (header safety)
        filename = new string(filename.Where(c => !char.IsControl(c)).ToArray());

        // ASCII fallback for legacy clients
        var chars = filename.Select(c => (c >= 32 && c <= 126 && c != '"' && c != '\\' && c != ';') ? c : '_');
        var ascii = new string(chars.ToArray());

        // RFC 5987 UTF-8 filename
        var utf8 = Uri.EscapeDataString(filename);

        // return
        var type = shouldDownload ? "attachment" : "inline";
        return $"{type}; filename=\"{ascii}\"; filename*=UTF-8''{utf8}";
    }

    private async Task<Stream> GetPar2PreviewStream(IStoreItem item)
    {
        Response.Headers.ContentType = "text/plain";
        await using var stream = await item.GetReadableStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var fileDescriptors = await Par2.ReadFileDescriptions(stream, HttpContext.RequestAborted).GetAllAsync()
            .ConfigureAwait(false);
        return new MemoryStream(Encoding.UTF8.GetBytes(fileDescriptors.ToIndentedJson()));
    }
}
