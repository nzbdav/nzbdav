using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ExcludeSync;

/// <summary>
/// GET  <c>/api/exclude-sync</c> — current per-URL sync status (no network).
/// POST <c>/api/exclude-sync</c> — force an immediate refresh of every configured URL,
/// then return the resulting status. Used by the "Sync now" button in settings.
/// </summary>
[ApiController]
[Route("api/exclude-sync")]
public class ExcludeSyncController(SearchExcludeSyncService syncService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpMethods.IsPost(HttpContext.Request.Method))
        {
            var refreshed = await syncService
                .RefreshAllAsync(force: true, HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new ExcludeSyncResponse { Status = true, Urls = refreshed });
        }

        return Ok(new ExcludeSyncResponse { Status = true, Urls = syncService.GetStatus() });
    }
}
