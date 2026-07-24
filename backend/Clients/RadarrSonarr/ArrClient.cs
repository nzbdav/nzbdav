using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class ArrClient(string host, string apiKey)
{
    protected static readonly HttpClient HttpClient = new();

    public string Host { get; } = host;
    private string ApiKey { get; } = apiKey;
    private const string BasePath = "/api/v3";

    public Task<ArrApiInfoResponse> GetApiInfo(CancellationToken ct = default) =>
        GetRoot<ArrApiInfoResponse>($"/api", ct);

    public virtual Task<bool> RemoveAndSearch(string symlinkOrStrmPath) =>
        throw new InvalidOperationException();

    public virtual Task<List<ArrRootFolder>> GetRootFolders() =>
        GetRootFolders(CancellationToken.None);

    public virtual Task<List<ArrRootFolder>> GetRootFolders(CancellationToken ct) =>
        Get<List<ArrRootFolder>>($"/rootfolder", ct);

    public Task<List<ArrDownloadClient>> GetDownloadClientsAsync(CancellationToken ct = default) =>
        Get<List<ArrDownloadClient>>($"/downloadClient", ct);

    public Task<ArrCommand> RefreshMonitoredDownloads(CancellationToken ct = default) =>
        CommandAsync(new { name = "RefreshMonitoredDownloads" }, ct);

    public Task<ArrQueueStatus> GetQueueStatusAsync(CancellationToken ct = default) =>
        Get<ArrQueueStatus>($"/queue/status", ct);

    public Task<ArrQueue<ArrQueueRecord>> GetQueueAsync(CancellationToken ct = default) =>
        Get<ArrQueue<ArrQueueRecord>>($"/queue?protocol=usenet&pageSize=5000", ct);

    public async Task<int> GetQueueCountAsync() =>
        (await Get<ArrQueue<ArrQueueRecord>>($"/queue?pageSize=1")).TotalRecords;

    public Task<HttpStatusCode> DeleteQueueRecord(int id, DeleteQueueRecordRequest request) =>
        Delete($"/queue/{id}", request.GetQueryParams());

    public Task<HttpStatusCode> DeleteQueueRecord(int id, ArrConfig.QueueAction request) =>
        request is not ArrConfig.QueueAction.DoNothing
            ? Delete($"/queue/{id}", new DeleteQueueRecordRequest(request).GetQueryParams())
            : Task.FromResult(HttpStatusCode.OK);

    public Task<ArrCommand> CommandAsync(object command, CancellationToken ct = default) =>
        Post<ArrCommand>($"/command", command, ct);

    protected Task<T> Get<T>(string path, CancellationToken ct = default) =>
        GetRoot<T>($"{BasePath}{path}", ct);

    protected async Task<T> GetRoot<T>(string rootPath, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}{rootPath}");
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct) ?? throw new NullReferenceException();
    }

    protected async Task<T> Post<T>(string path, object body, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, GetRequestUri(path));
        var jsonBody = JsonSerializer.Serialize(body);
        request.Content = new StringContent(jsonBody, new MediaTypeHeaderValue("application/json"));
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct) ?? throw new NullReferenceException();
    }

    protected async Task<HttpStatusCode> Delete(string path, Dictionary<string, string>? queryParams = null, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, GetRequestUri(path, queryParams));
        using var response = await SendAsync(request, ct);
        return response.StatusCode;
    }

    private string GetRequestUri(string path, Dictionary<string, string>? queryParams = null)
    {
        queryParams ??= new Dictionary<string, string>();
        var resource = $"{Host}{BasePath}{path}";
        var query = queryParams.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
        var queryString = string.Join("&", query);
        if (queryString.Length > 0) resource = $"{resource}?{queryString}";
        return resource;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Add("X-Api-Key", ApiKey);
        return HttpClient.SendAsync(request, ct);
    }
}
