using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

public class GetWebdavItemRequest
{
    public string Item { get; init; }
    public long? RangeStart { get; init; }
    public long? RangeEnd { get; init; }
    // RFC 7233 suffix-length: "bytes=-N" means the last N bytes of the file.
    // The controller resolves this to a concrete start/end once fileSize is known.
    public long? SuffixLength { get; init; }
    public bool ShouldDownload { get; init; }

    public GetWebdavItemRequest(HttpContext context)
    {
        // normalize path
        var path = context.Request.Path.Value;
        if (path.StartsWith("/")) path = path[1..];
        if (path.StartsWith("view")) path = path[4..];
        if (path.StartsWith("/")) path = path[1..];
        Item = path;

        // determine whether to download
        ShouldDownload = context.GetQueryParam("download")?.ToLower() == "true";

        // authenticate the downloadKey
        var downloadKey = context.Request.Query["downloadKey"];
        var configManager = (ConfigManager)context.Items["configManager"]!;
        if (!VerifyDownloadKey(downloadKey, Item, configManager))
            throw new UnauthorizedAccessException("Invalid download key");

        // parse range header — three RFC 7233 forms:
        //   bytes=N-     → from byte N to EOF
        //   bytes=N-M    → bytes N..M inclusive
        //   bytes=-N     → last N bytes (a "suffix" range; resolved against fileSize)
        // The old `Split("-", RemoveEmptyEntries)` silently dropped the leading
        // empty token of the suffix form, mis-parsing "bytes=-65536" as
        // RangeStart=65536, which served mid-file data to players asking for the
        // MP4 MOOV / MKV SeekHead at the end of the file.
        var rangeHeader = context.Request.Headers["Range"].FirstOrDefault() ?? "";
        if (!rangeHeader.StartsWith("bytes=")) return;
        var spec = rangeHeader[6..];
        if (spec.StartsWith("-"))
        {
            SuffixLength = long.Parse(spec[1..]);
        }
        else
        {
            var parts = spec.Split("-");
            RangeStart = long.Parse(parts[0]);
            if (parts.Length > 1 && parts[1].Length > 0)
                RangeEnd = long.Parse(parts[1]);
        }
    }

    private static bool VerifyDownloadKey(string? downloadKey, string path, ConfigManager configManager)
    {
        if (path.StartsWith(".ids"))
        {
            // strm streams link items by id and use a different download key
            var strmKey = configManager.GetStrmKey();
            if (VerifyDownloadKey(downloadKey, strmKey, path))
                return true;
        }

        var apiKey = EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
        return VerifyDownloadKey(downloadKey, apiKey, path);
    }

    private static bool VerifyDownloadKey(string? downloadKey, string apiKey, string path)
    {
        return downloadKey.FixedTimeEquals(GenerateDownloadKey(apiKey, path))
            || downloadKey.FixedTimeEquals(GenerateLegacyDownloadKey(apiKey, path));
    }

    public static string GenerateDownloadKey(string apiKey, string path)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var hashBytes = HMACSHA256.HashData(keyBytes, pathBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string GenerateLegacyDownloadKey(string apiKey, string path)
    {
        var input = $"{path}_{apiKey}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
