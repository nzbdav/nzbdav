using Microsoft.AspNetCore.Http;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Extensions;

public static class HttpContextExtensions
{
    public static string? GetRequestParam(this HttpContext httpContext, string key)
    {
        return httpContext.GetQueryParam(key)
            ?? httpContext.GetFormParam(key);
    }

    public static string? GetQueryParam(this HttpContext httpContext, string name)
    {
        return StringUtil.EmptyToNull(httpContext.Request.Query[name].FirstOrDefault());
    }

    public static string? GetFormParam(this HttpContext httpContext, string name)
    {
        return httpContext.Request.HasFormContentType
            ? StringUtil.EmptyToNull(httpContext.Request.Form[name].FirstOrDefault())
            : null;
    }

    public static IEnumerable<string> GetQueryParamValues(this HttpContext httpContext, string name)
    {
        return httpContext.Request.Query[name]
            .Where(x => x is not null)
            .Select(x => x!);
    }

    public static string? GetRequestApiKey(this HttpContext httpContext)
    {
        return httpContext.Request.Headers["x-api-key"].FirstOrDefault()
            ?? httpContext.GetRequestParam("apikey");
    }

    public static string GetPublicBaseUrl(this HttpContext httpContext, string configuredBaseUrl)
    {
        var trimmed = configuredBaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(trimmed) && trimmed != "http://localhost:3000")
            return trimmed;
        var scheme = httpContext.Request.Headers["X-Forwarded-Proto"].FirstOrDefault()
                     ?? httpContext.Request.Scheme;
        var host = httpContext.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                   ?? httpContext.Request.Host.Value;
        return $"{scheme}://{host}";
    }
}
