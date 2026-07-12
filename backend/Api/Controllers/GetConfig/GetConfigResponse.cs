using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetConfig;

public class GetConfigResponse : BaseApiResponse
{
    public List<ConfigItem> ConfigItems { get; init; } = new();
}
