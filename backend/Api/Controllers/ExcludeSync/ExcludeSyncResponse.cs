using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ExcludeSync;

public sealed class ExcludeSyncResponse : BaseApiResponse
{
    public IReadOnlyList<ExcludeSyncStatus> Urls { get; set; } = Array.Empty<ExcludeSyncStatus>();
}
