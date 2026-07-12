using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileRequest()
{
    public string FileName { get; init; }
    public string? ContentType { get; init; }
    public Stream NzbFileStream { get; init; }
    public string Category { get; init; }
    public QueueItem.PriorityOption Priority { get; init; }
    public QueueItem.PostProcessingOption PostProcessing { get; init; }
    public DateTime? PauseUntil { get; init; }
    public string? IndexerName { get; init; }
    public string? ContentGroupKey { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<AddFileRequest> New(HttpContext context, ConfigManager configManager)
    {
        var file =
            context.Request.Form.Files["nzbFile"] ??
            context.Request.Form.Files["name"] ??
            throw new BadHttpRequestException("Invalid nzbFile/name param");

        // Prefer the nzbname query/form param (set by some SAB clients); fall back to the
        // form file's filename. Without this, file.FileName can be null/empty and downstream
        // Regex.Match throws a 500. Adopted from elfhosted/rebased-v3.
        var fileName = ResolveFileName(context.GetRequestParam("nzbname"), file.FileName);

        return new AddFileRequest()
        {
            FileName = fileName,
            ContentType = file.ContentType,
            NzbFileStream = file.OpenReadStream(),
            Category = context.GetRequestParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    /// <summary>
    /// Resolve the NZB filename from an optional SAB <c>nzbname</c> param and the uploaded file name.
    /// </summary>
    internal static string ResolveFileName(string? nzbName, string? formFileName)
    {
        var fileName = !string.IsNullOrWhiteSpace(nzbName)
            ? (nzbName.EndsWith(".nzb", StringComparison.OrdinalIgnoreCase) ? nzbName : $"{nzbName}.nzb")
            : formFileName;

        if (string.IsNullOrWhiteSpace(fileName))
            throw new BadHttpRequestException("NZB filename could not be determined.");

        return fileName;
    }

    protected static QueueItem.PriorityOption MapPriorityOption(string? priority)
    {
        return priority switch
        {
            "-100" => QueueItem.PriorityOption.Normal,
            "-3" => QueueItem.PriorityOption.Duplicate,
            "-2" => QueueItem.PriorityOption.Paused,
            "-1" => QueueItem.PriorityOption.Low,
            "0" => QueueItem.PriorityOption.Normal,
            "1" => QueueItem.PriorityOption.High,
            "2" => QueueItem.PriorityOption.Force,
            null => QueueItem.PriorityOption.Normal,
            _ => throw new BadHttpRequestException("Invalid priority")
        };
    }

    protected static QueueItem.PostProcessingOption MapPostProcessingOption(string? postProcessing)
    {
        return postProcessing switch
        {
            "-1" => QueueItem.PostProcessingOption.None,
            "0" => QueueItem.PostProcessingOption.None,
            "1" => QueueItem.PostProcessingOption.Repair,
            "2" => QueueItem.PostProcessingOption.RepairUnpack,
            "3" => QueueItem.PostProcessingOption.RepairUnpackDelete,
            null => QueueItem.PostProcessingOption.None,
            _ => throw new BadHttpRequestException("Invalid pp param")
        };
    }
}
