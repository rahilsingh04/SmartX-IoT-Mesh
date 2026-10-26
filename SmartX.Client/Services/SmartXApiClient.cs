using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Client.Models;

namespace SmartX.Client.Services;

/// <summary>
/// Typed HttpClient wrapper the Blazor dashboard uses to talk to the SmartX.Api gateway.
/// Every call is asynchronous so the UI thread is never blocked.
/// </summary>
public class SmartXApiClient
{
    private readonly HttpClient _http;

    public SmartXApiClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;

        // The address the BROWSER can reach (used for download links). Inside Docker the client
        // talks to http://smartx-api:5080 but the user's browser must use http://localhost:5080.
        var publicUrl = configuration["SmartXApi:PublicUrl"];
        PublicBaseUrl = string.IsNullOrWhiteSpace(publicUrl)
            ? (http.BaseAddress?.ToString() ?? "http://localhost:5080/")
            : publicUrl;
        if (!PublicBaseUrl.EndsWith('/')) PublicBaseUrl += "/";
    }

    public string PublicBaseUrl { get; }

    // ------------------------------------------------------------------ Part 1

    public async Task<bool> IsApiOnlineAsync()
    {
        try
        {
            var res = await _http.GetAsync("/api/health");
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<SensorProfileDto>> GetSensorsAsync() =>
        await _http.GetFromJsonAsync<List<SensorProfileDto>>("/api/sensors") ?? new();

    public async Task<DeploymentNodeDto?> GetFacilityTreeAsync() =>
        await _http.GetFromJsonAsync<DeploymentNodeDto>("/api/facility-tree");

    public async Task<List<SensorReadingDto>> GetRecentReadingsAsync(int take = 50) =>
        await _http.GetFromJsonAsync<List<SensorReadingDto>>($"/api/telemetry/recent?take={take}") ?? new();

    public async Task<(int capacity, int count)> GetBufferStatusAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<BufferStatusDto>("/api/telemetry/buffer-status");
            return status is null ? (0, 0) : (status.Capacity, status.Count);
        }
        catch
        {
            return (0, 0);
        }
    }

    private class BufferStatusDto
    {
        public int Capacity { get; set; }
        public int Count { get; set; }
        public bool IsFull { get; set; }
    }

    public async Task<(bool ok, string message)> RegisterSensorAsync(SensorRegistrationRequest request)
    {
        var res = await _http.PostAsJsonAsync("/api/sensors", request);
        var message = await ReadMessageAsync(res);
        return (res.IsSuccessStatusCode, message);
    }

    public Task<HttpResponseMessage> IngestFloatAsync(string deviceId, float value) =>
        _http.PostAsJsonAsync("/api/telemetry/float", new TelemetryIngestRequest<float>(deviceId, value));

    public Task<HttpResponseMessage> IngestIntAsync(string deviceId, int value) =>
        _http.PostAsJsonAsync("/api/telemetry/int", new TelemetryIngestRequest<int>(deviceId, value));

    public Task<HttpResponseMessage> IngestBoolAsync(string deviceId, bool value) =>
        _http.PostAsJsonAsync("/api/telemetry/bool", new TelemetryIngestRequest<bool>(deviceId, value));

    public async Task<AttachedFileDto?> UploadAttachmentAsync(string sensorId, Stream fileStream, string fileName, string contentType)
    {
        // Some files (.log, .cfg) arrive from the browser with an empty content type.
        var type = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;

        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        content.Add(streamContent, "file", fileName);

        var res = await _http.PostAsync($"/api/sensors/{sensorId}/attachments", content);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<AttachedFileDto>();
    }

    public string AttachmentDownloadUrl(string sensorId, string attachmentId) =>
        $"{PublicBaseUrl}api/sensors/{sensorId}/attachments/{attachmentId}/download";

    // ------------------------------------------------------------------ Part 2

    public async Task<StreamStatsDto> GetStreamStatsAsync() =>
        await _http.GetFromJsonAsync<StreamStatsDto>("/api/stream/stats") ?? new();

    public async Task<List<DeviceViewDto>> QueryDevicesAsync(string? text, string? category, string? state)
    {
        var url = "/api/stream/devices"
                  + $"?q={Uri.EscapeDataString(text ?? "")}"
                  + $"&category={Uri.EscapeDataString(category ?? "")}"
                  + $"&state={Uri.EscapeDataString(state ?? "")}";
        return await _http.GetFromJsonAsync<List<DeviceViewDto>>(url) ?? new();
    }

    public async Task<List<HistoryPointDto>> GetHistoryAsync(string mac, int take = 120) =>
        await _http.GetFromJsonAsync<List<HistoryPointDto>>(
            $"/api/stream/history/{Uri.EscapeDataString(mac)}?take={take}") ?? new();

    public async Task<AlertsViewDto> GetAlertsAsync() =>
        await _http.GetFromJsonAsync<AlertsViewDto>("/api/stream/alerts") ?? new();

    public async Task<InsightsViewDto> GetInsightsAsync() =>
        await _http.GetFromJsonAsync<InsightsViewDto>("/api/insights") ?? new();

    public async Task<List<CommandRecordDto>> GetCommandHistoryAsync() =>
        await _http.GetFromJsonAsync<List<CommandRecordDto>>("/api/commands/history") ?? new();

    public async Task<(bool ok, string message)> IssueCommandAsync(string target, string action)
    {
        var res = await _http.PostAsJsonAsync("/api/commands", new { target, action });
        if (!res.IsSuccessStatusCode) return (false, await ReadMessageAsync(res));

        var record = await res.Content.ReadFromJsonAsync<CommandRecordDto>();
        return (true, $"{record?.TargetLabel}: {record?.Action} sent ({record?.Changes.Count} device(s)).");
    }

    public async Task<(bool ok, string message)> UndoLastCommandAsync()
    {
        var res = await _http.PostAsync("/api/commands/undo", null);
        if (!res.IsSuccessStatusCode) return (false, await ReadMessageAsync(res));

        var record = await res.Content.ReadFromJsonAsync<CommandRecordDto>();
        return (true, $"Undone: {record?.TargetLabel} {record?.Action}.");
    }

    public async Task LogSearchAsync(string text)
    {
        await _http.PostAsJsonAsync("/api/insights/events", new { kind = "search", text });
    }

    public async Task<DisconnectResultDto?> ReportDisconnectAsync(string mac)
    {
        var res = await _http.PostAsync($"/api/stream/disconnect/{Uri.EscapeDataString(mac)}", null);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<DisconnectResultDto>();
    }

    public async Task<bool> SendBatchAsync(IReadOnlyCollection<BatchItemDto> items)
    {
        var res = await _http.PostAsJsonAsync("/api/telemetry/batch", items);
        return res.IsSuccessStatusCode;
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage res)
    {
        try
        {
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var message))
                return message.GetString() ?? "";
        }
        catch
        {
            // fall through to the generic text below
        }

        return res.IsSuccessStatusCode ? "OK" : $"Request failed ({(int)res.StatusCode}).";
    }
}
