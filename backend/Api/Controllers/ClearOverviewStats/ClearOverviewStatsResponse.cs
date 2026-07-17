using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.ClearOverviewStats;

public class ClearOverviewStatsResponse : BaseApiResponse
{
    [JsonPropertyName("deletedRows")]
    public required int DeletedRows { get; init; }
}
