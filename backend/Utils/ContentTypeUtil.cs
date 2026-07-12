using Microsoft.AspNetCore.StaticFiles;

namespace NzbWebDAV.Utils;

public static class ContentTypeUtil
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider;

    static ContentTypeUtil()
    {
        // ReSharper disable once UseObjectOrCollectionInitializer
        ContentTypeProvider = new FileExtensionContentTypeProvider();
        ContentTypeProvider.Mappings[".flac"] = "audio/flac";
        ContentTypeProvider.Mappings[".mkv"] = "video/x-matroska";
        ContentTypeProvider.Mappings[".mk3d"] = "video/x-matroska";
        ContentTypeProvider.Mappings[".m4v"] = "video/x-m4v";
        ContentTypeProvider.Mappings[".ts"] = "video/mp2t";
        ContentTypeProvider.Mappings[".m2ts"] = "video/mp2t";
        ContentTypeProvider.Mappings[".mts"] = "video/mp2t";
        ContentTypeProvider.Mappings[".divx"] = "video/divx";
        ContentTypeProvider.Mappings[".rmvb"] = "application/vnd.rn-realmedia-vbr";
    }

    public static string GetContentType(string fileName)
    {
        return !ContentTypeProvider.TryGetContentType(Path.GetFileName(fileName), out var contentType)
            ? "application/octet-stream"
            : contentType;
    }
}
