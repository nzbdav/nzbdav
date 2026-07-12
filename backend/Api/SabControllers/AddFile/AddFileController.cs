using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore
    };

    public async Task<AddFileResponse> AddFileAsync(AddFileRequest request)
    {
        var id = Guid.NewGuid();
        var category = StringUtil.EmptyToNull(request.Category)
                       ?? configManager.GetManualUploadCategory();

        // write the file to the blob-store
        await using var stream = request.NzbFileStream;
        await BlobStore.WriteBlob(id, stream);

        // save the queue item to the database
        QueueItem? queueItem;
        try
        {
            // backup the nzb file if enabled
            if (configManager.IsNzbBackupEnabled())
            {
                var backupLocation = configManager.GetNzbBackupLocation();
                if (backupLocation != null)
                {
                    await BackupNzbAsync(id, request.FileName, category, backupLocation);
                }
            }

            // compute the total segment bytes
            await using var nzbFileStream = BlobStore.ReadBlob(id);
            var totalSegmentBytes = ComputeTotalSegmentBytes(nzbFileStream);

            // create the queue item record
            queueItem = new QueueItem
            {
                Id = id,
                CreatedAt = DateTime.Now,
                FileName = request.FileName,
                JobName = FilenameUtil.GetJobName(request.FileName),
                NzbFileSize = nzbFileStream.Length,
                TotalSegmentBytes = totalSegmentBytes,
                Category = category,
                Priority = request.Priority,
                PostProcessing = request.PostProcessing,
                PauseUntil = request.PauseUntil,
                IndexerName = request.IndexerName,
                ContentGroupKey = request.ContentGroupKey,
            };

            // record the original NZB filename so it can be served at download time
            var nzbName = new NzbName
            {
                Id = id,
                FileName = request.FileName
            };

            // save
            dbClient.Ctx.QueueItems.Add(queueItem);
            dbClient.Ctx.NzbNames.Add(nzbName);
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
            _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"]);
        }
        catch
        {
            // in case of any errors writing to the database
            // delete the nzb file blob
            BlobStore.Delete(id);
            throw;
        }

        // inform the frontend that a new item was added to the queue
        var message = GetQueueResponse.QueueSlot.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);

        // awaken the queue if it is sleeping
        queueManager.AwakenQueue(request.PauseUntil);

        // return response
        return new AddFileResponse()
        {
            Status = true,
            NzoIds = [queueItem.Id.ToString()],
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await AddFileRequest.New(httpContext, configManager).ConfigureAwait(false);
        return Ok(await AddFileAsync(request).ConfigureAwait(false));
    }

    private static async Task BackupNzbAsync(Guid id, string fileName, string category, string backupLocation)
    {
        try
        {
            ValidateBackupCategory(category);

            var backupRoot = Path.GetFullPath(backupLocation);
            var backupRootPrefix = Path.EndsInDirectorySeparator(backupRoot)
                ? backupRoot
                : backupRoot + Path.DirectorySeparatorChar;
            if (!Directory.Exists(backupRoot))
                Directory.CreateDirectory(backupRoot);

            var destDir = Path.GetFullPath(Path.Combine(backupRootPrefix, category));
            if (!destDir.StartsWith(backupRootPrefix, StringComparison.Ordinal))
                throw new ArgumentException("The NZB backup category must stay within the configured directory.");
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var destDirPrefix = Path.EndsInDirectorySeparator(destDir)
                ? destDir
                : destDir + Path.DirectorySeparatorChar;
            var leafName = Path.GetFileName(fileName);
            var baseName = Path.GetFileNameWithoutExtension(leafName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = id.ToString();
            var ext = Path.GetExtension(leafName);
            if (string.IsNullOrEmpty(ext)) ext = ".nzb";

            var destPath = Path.GetFullPath(Path.Combine(destDirPrefix, $"{baseName}{ext}"));
            if (!destPath.StartsWith(destDirPrefix, StringComparison.Ordinal))
                throw new ArgumentException("The NZB backup file must stay within its category directory.");
            var counter = 2;
            while (System.IO.File.Exists(destPath))
            {
                destPath = Path.GetFullPath(Path.Combine(destDirPrefix, $"{baseName} ({counter}){ext}"));
                if (!destPath.StartsWith(destDirPrefix, StringComparison.Ordinal))
                    throw new ArgumentException("The NZB backup file must stay within its category directory.");
                counter++;
            }

            await using var src = BlobStore.ReadBlob(id);
            await using var dst = System.IO.File.Create(destPath);
            await src.CopyToAsync(dst);
        }
        catch (Exception e)
        {
            throw new Exception($"Could not save nzb to `{backupLocation}`", e);
        }
    }

    private static void ValidateBackupCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category) ||
            Path.IsPathRooted(category) ||
            category is "." or ".." ||
            category.Contains('/') ||
            category.Contains('\\') ||
            category.Contains('\0'))
        {
            throw new ArgumentException("The NZB backup category must be a single directory name.", nameof(category));
        }
    }

    private static long ComputeTotalSegmentBytes(Stream stream)
    {
        long totalBytes = 0;
        using var reader = XmlReader.Create(stream, XmlSettings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "segment") continue;
            var bytesAttr = reader.GetAttribute("bytes");
            if (bytesAttr != null && long.TryParse(bytesAttr, out var bytes))
            {
                totalBytes += bytes;
            }
        }

        return totalBytes;
    }
}
