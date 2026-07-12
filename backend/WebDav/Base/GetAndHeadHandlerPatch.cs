using System.Buffers;
using Microsoft.AspNetCore.Http;
using NWebDav.Server;
using NWebDav.Server.Handlers;
using NWebDav.Server.Helpers;
using NWebDav.Server.Props;
using NWebDav.Server.Stores;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Services;

namespace NzbWebDAV.WebDav.Base;

/// <summary>
/// Implementation of the GET and HEAD method.
/// </summary>
/// <remarks>
/// The specification of the WebDAV GET and HEAD methods for collections
/// can be found in the
/// <see href="http://www.webdav.org/specs/rfc2518.html#rfc.section.8.4">
/// WebDAV specification
/// </see>.
/// </remarks>
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly ProviderUsageTracker _providerUsageTracker;
    private readonly ActiveReadRegistry _activeReadRegistry;

    public GetAndHeadHandlerPatch(
        IStore store,
        ProviderUsageTracker providerUsageTracker,
        ActiveReadRegistry activeReadRegistry)
    {
        _store = store;
        _providerUsageTracker = providerUsageTracker;
        _activeReadRegistry = activeReadRegistry;
    }

    /// <summary>
    /// Handle a GET or HEAD request.
    /// </summary>
    /// <param name="httpContext">
    /// The HTTP context of the request.
    /// </param>
    /// <returns>
    /// A task that represents the asynchronous GET or HEAD operation. The
    /// task will always return <see langword="true"/> upon completion.
    /// </returns>
    public async Task<bool> HandleRequestAsync(HttpContext httpContext)
    {
        // Obtain request and response
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Determine if we are invoked as HEAD
        var isHeadRequest = request.Method == HttpMethods.Head;

        // Determine the requested range
        var range = request.GetRange();
        var copyStart = 0L;
        long? copyEnd = null;

        // Obtain the WebDAV collection
        var entry = await _store.GetItemAsync(request.GetUri(), httpContext.RequestAborted).ConfigureAwait(false);
        if (entry == null)
        {
            // Set status to not found
            response.SetStatus(DavStatusCode.NotFound);
            return true;
        }

        // ETag might be used for a conditional request
        string? etag = null;

        // Add non-expensive headers based on properties
        var propertyManager = entry.PropertyManager;
        if (propertyManager != null)
        {
            // Add Last-Modified header
            var lastModifiedUtc = (string?)await propertyManager.GetPropertyAsync(entry, DavGetLastModified<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (lastModifiedUtc != null)
                response.Headers.LastModified = lastModifiedUtc;

            // Add ETag
            etag = (string?)await propertyManager.GetPropertyAsync(entry, DavGetEtag<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (etag != null)
                response.Headers.ETag = etag;

            // Add type
            var contentType = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentType<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentType != null)
                response.ContentType = contentType;

            // Add language
            var contentLanguage = (string?)await propertyManager.GetPropertyAsync(entry, DavGetContentLanguage<IStoreItem>.PropertyName, true, httpContext.RequestAborted).ConfigureAwait(false);
            if (contentLanguage != null)
                response.Headers.ContentLanguage = contentLanguage;
        }

        // Stream the actual entry
        var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream != Stream.Null)
            {
                // Set the response
                response.SetStatus(DavStatusCode.Ok);

                // Set the expected content length
                try
                {
                    // We can only specify the Content-Length header if the
                    // length is known (this is typically true for seekable streams)
                    if (stream.CanSeek)
                    {
                        // Add a header that we accept ranges (bytes only)
                        response.Headers.AcceptRanges = "bytes";

                        // Determine the total length
                        var length = stream.Length;

                        // Check if a range was specified
                        if (range != null)
                        {
                            long start;
                            long end;
                            if (!range.Start.HasValue && range.End.HasValue)
                            {
                                var suffixLength = range.End.Value;
                                start = suffixLength > 0 ? Math.Max(0, length - suffixLength) : length;
                                end = length - 1;
                            }
                            else
                            {
                                start = range.Start ?? 0;
                                end = Math.Min(range.End ?? length - 1, length - 1);
                            }

                            // Return 416 if the range start is beyond the end of the file
                            if (start < 0 || start > end)
                            {
                                response.Headers.ContentRange = $"bytes */{stream.Length}";
                                response.SetStatus((DavStatusCode)416);
                                return true;
                            }

                            length = end - start + 1;
                            copyStart = start;
                            copyEnd = end;

                            // Write the range
                            response.Headers.ContentRange = $"bytes {start}-{end}/{stream.Length}";
                            response.SetStatus(DavStatusCode.PartialContent);
                        }

                        // Set the header, so the client knows how much data is required
                        response.ContentLength = length;
                    }
                }
                catch (NotSupportedException)
                {
                    // If the content length is not supported, then we just skip it
                }

                // Do not return the actual item data if ETag matches
                if (etag != null && request.Headers.IfNoneMatch == etag)
                {
                    response.ContentLength = 0;
                    response.SetStatus(DavStatusCode.NotModified);
                    return true;
                }

                // HEAD method doesn't require the actual item data
                if (!isHeadRequest)
                {
                    var path = request.GetUri().AbsolutePath;
                    var clientKey = $"{httpContext.Connection.RemoteIpAddress}|{request.Headers.UserAgent}";
                    // DatabaseStoreIdFile.Name returns the GUID (it backs rclone symlink
                    // targets), so prefer FriendlyName when that's what we got.
                    var fileName = entry switch
                    {
                        NzbWebDAV.WebDav.DatabaseStoreIdFile idFile => idFile.FriendlyName,
                        _ => !string.IsNullOrEmpty(entry.Name) ? entry.Name : System.IO.Path.GetFileName(path)
                    };
                    var sessionId = _activeReadRegistry.GetOrCreate(
                        path, clientKey, fileName, stream.CanSeek ? stream.Length : null);
                    using var scope = _providerUsageTracker.BeginScope(sessionId);
                    using var metricsScope = MultiProviderNntpClient.BeginReadSessionScope(sessionId);
                    await CopyToAsync(stream, response.Body, copyStart, copyEnd,
                        (n, pos) => _activeReadRegistry.Touch(sessionId, n, pos),
                        httpContext.RequestAborted).ConfigureAwait(false);
                }
            }
            else
            {
                // Set the response
                response.SetStatus(DavStatusCode.NoContent);
            }
        }
        return true;
    }

    private async Task CopyToAsync(
        Stream src,
        Stream dest,
        long start,
        long? end,
        Action<long, long>? onBytesServed,
        CancellationToken cancellationToken)
    {
        // Skip to the first offset
        if (start > 0)
        {
            // We prefer seeking instead of draining data
            if (!src.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");

            src.Seek(start, SeekOrigin.Begin);
        }

        // Determine the number of bytes to read
        var bytesToRead = end - start + 1 ?? long.MaxValue;

        // Read in 64KB blocks without allocating a large array for every request.
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var position = start;
        try
        {
            // Copy, until we don't get any data anymore
            while (bytesToRead > 0)
            {
                // Read the requested bytes into memory
                var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
                var bytesRead = await src.ReadAsync(
                    buffer.AsMemory(0, requestedBytes), cancellationToken).ConfigureAwait(false);

                // We're done, if we cannot read any data anymore
                if (bytesRead == 0)
                    return;

                // Write the data to the destination stream
                await dest.WriteAsync(
                    buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

                // Report chunk size + new absolute file position so dashboards can
                // surface real playback location (not cumulative transferred bytes).
                position += bytesRead;
                onBytesServed?.Invoke(bytesRead, position);

                // Decrement the number of bytes left to read
                bytesToRead -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
