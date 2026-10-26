using SmartX.Api.Models;
using SmartX.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<ActionEngine>();
builder.Services.AddSingleton<StreamEngine>();
builder.Services.AddSingleton<FileEncryptionService>();
builder.Services.AddHostedService<TelemetryProcessorService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddCors(options =>
{
    options.AddPolicy("SmartXClient", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("SmartXClient");

// Encrypted attachments live OUTSIDE wwwroot: they are never reachable as static files,
// only through the decrypting download endpoint below.
var secureUploadsRoot = Path.Combine(app.Environment.ContentRootPath, "SecureUploads");
Directory.CreateDirectory(secureUploadsRoot);

// ---------------------------------------------------------------------------
// Health
// ---------------------------------------------------------------------------
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "Smart-X Ingestion Gateway online",
    utc = DateTimeOffset.UtcNow
}));

// ---------------------------------------------------------------------------
// PART 1 - Sensor registration (recursive deployment-path validation)
// ---------------------------------------------------------------------------
app.MapGet("/api/sensors", (TelemetryStore store) => Results.Ok(store.Sensors));

app.MapGet("/api/facility-tree", (TelemetryStore store) => Results.Ok(store.FacilityTree));

app.MapPost("/api/sensors", (SensorRegistrationRequest req, TelemetryStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.MacAddress) || string.IsNullOrWhiteSpace(req.Location)
        || string.IsNullOrWhiteSpace(req.DeploymentPath))
    {
        return Results.BadRequest(new { message = "MAC address, location and deployment path are required." });
    }

    var profile = new SensorProfile
    {
        MacAddress = req.MacAddress.Trim(),
        Location = req.Location.Trim(),
        Category = req.Category,
        DeploymentPath = req.DeploymentPath.Trim(),
        Name = req.Name?.Trim() ?? ""
    };

    var (ok, message) = store.RegisterSensor(profile);
    return ok
        ? Results.Created($"/api/sensors/{profile.Id}", new { profile, message })
        : Results.BadRequest(new { message });
});

// ---------------------------------------------------------------------------
// Telemetry ingestion. Devices POST packets; the API only QUEUES them (Queue<T> for normal
// traffic, PriorityQueue for critical readings) and a background worker processes them.
// One endpoint per closed generic type: TelemetryPacket<float|int|bool>, no boxing.
// ---------------------------------------------------------------------------
app.MapPost("/api/telemetry/float", (TelemetryIngestRequest<float> req, StreamEngine engine) =>
{
    var packet = new TelemetryPacket<float> { DeviceId = req.DeviceId, Type = ReadingType.Float, Value = req.Value };
    return Results.Ok(engine.Submit(packet));
});

app.MapPost("/api/telemetry/int", (TelemetryIngestRequest<int> req, StreamEngine engine) =>
{
    var packet = new TelemetryPacket<int> { DeviceId = req.DeviceId, Type = ReadingType.Integer, Value = req.Value };
    return Results.Ok(engine.Submit(packet));
});

app.MapPost("/api/telemetry/bool", (TelemetryIngestRequest<bool> req, StreamEngine engine) =>
{
    var packet = new TelemetryPacket<bool> { DeviceId = req.DeviceId, Type = ReadingType.Boolean, Value = req.Value };
    return Results.Ok(engine.Submit(packet));
});

// Gateway-style batch upload: many packets in one request, each classified individually.
app.MapPost("/api/telemetry/batch", (List<BatchItem> items, StreamEngine engine) =>
{
    var queued = 0;
    var priority = 0;

    foreach (var item in items)
    {
        if (string.IsNullOrWhiteSpace(item.DeviceId)) continue;

        SubmitResult result;
        switch ((item.Type ?? "float").ToLowerInvariant())
        {
            case "int":
                result = engine.Submit(new TelemetryPacket<int>
                    { DeviceId = item.DeviceId, Type = ReadingType.Integer, Value = (int)item.Value });
                break;
            case "bool":
                result = engine.Submit(new TelemetryPacket<bool>
                    { DeviceId = item.DeviceId, Type = ReadingType.Boolean, Value = item.Value >= 0.5 });
                break;
            default:
                result = engine.Submit(new TelemetryPacket<float>
                    { DeviceId = item.DeviceId, Type = ReadingType.Float, Value = (float)item.Value });
                break;
        }

        queued++;
        if (result.Critical) priority++;
    }

    return Results.Ok(new { queued, sentToPriorityQueue = priority });
});

app.MapGet("/api/telemetry/recent", (TelemetryStore store, int take = 50) =>
    Results.Ok(store.RecentReadings(take)));

app.MapGet("/api/telemetry/buffer-status", (TelemetryStore store) =>
{
    var (capacity, count) = store.ReadingLogBufferStatus;
    return Results.Ok(new { capacity, count, isFull = count == capacity });
});

// Operator-overloading demo: aggregates two devices' latest readings with SensorReading's + / - / >.
app.MapGet("/api/telemetry/aggregate", (string deviceA, string deviceB, TelemetryStore store) =>
{
    var recent = store.RecentReadings(2000);
    var a = recent.LastOrDefault(r => r.DeviceId == deviceA);
    var b = recent.LastOrDefault(r => r.DeviceId == deviceB);

    if (a.DeviceId is null || b.DeviceId is null)
        return Results.NotFound(new { message = "One or both devices have no recent readings." });

    var combined = a + b;
    var delta = a - b;
    var aIsGreater = a > b;

    return Results.Ok(new { a, b, combined, delta, aIsGreater });
});

// ---------------------------------------------------------------------------
// PART 2 - Real-time command stream
// ---------------------------------------------------------------------------
app.MapGet("/api/stream/stats", (StreamEngine engine) => Results.Ok(engine.GetStats()));

app.MapGet("/api/stream/devices", (StreamEngine engine, string? q, string? category, string? state) =>
    Results.Ok(engine.QueryDevices(q, category, state)));

app.MapGet("/api/stream/history/{mac}", (string mac, StreamEngine engine, int take = 120) =>
    Results.Ok(engine.History(mac, take)));

app.MapGet("/api/stream/alerts", (StreamEngine engine) => Results.Ok(engine.GetAlerts()));

// A device / gateway "last will" message saying a node went offline. Sending it repeatedly
// proves the HashSet suppresses duplicate disconnect alerts.
app.MapPost("/api/stream/disconnect/{mac}", (string mac, StreamEngine engine) =>
{
    var (found, raised, suppressed) = engine.ReportDisconnect(mac);
    if (!found) return Results.NotFound(new { message = $"Unknown device '{mac}'." });

    return Results.Ok(new
    {
        raised,
        duplicatesSuppressedTotal = suppressed,
        message = raised
            ? "Disconnect alert raised."
            : "Duplicate ignored: this node is already in the disconnected HashSet."
    });
});

// Manual override commands (Stack<T> history enables Undo).
app.MapPost("/api/commands", (CommandRequest req, StreamEngine engine) =>
{
    var (record, error) = engine.IssueCommand(req.Target, req.Action);
    return record is null
        ? Results.BadRequest(new { message = error })
        : Results.Ok(record);
});

app.MapPost("/api/commands/undo", (StreamEngine engine) =>
{
    var (record, error) = engine.UndoLast();
    return record is null
        ? Results.BadRequest(new { message = error })
        : Results.Ok(record);
});

app.MapGet("/api/commands/history", (StreamEngine engine) => Results.Ok(engine.CommandHistory()));

// Predictive action engine.
app.MapGet("/api/insights", (StreamEngine engine) => Results.Ok(engine.GetInsights()));

app.MapPost("/api/insights/events", (InsightEvent evt, ActionEngine actions) =>
{
    var text = (evt.Text ?? "").Trim().ToLowerInvariant();
    if (text.Length < 2) return Results.BadRequest(new { message = "Text is too short." });

    actions.Record("search:" + text);
    return Results.Ok(new { recorded = true });
});

// ---------------------------------------------------------------------------
// PART 1 - Encrypted media / log upload (multipart/form-data) and download
// ---------------------------------------------------------------------------
app.MapPost("/api/sensors/{sensorId}/attachments", async (
    string sensorId, HttpRequest request, TelemetryStore store, FileEncryptionService encryption) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { message = "Expected multipart/form-data." });

    var sensor = store.FindSensor(sensorId);
    if (sensor is null)
        return Results.NotFound(new { message = $"Sensor '{sensorId}' not found." });

    var form = await request.ReadFormAsync();
    var file = form.Files.FirstOrDefault();
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "No file was received." });

    const long maxBytes = 25 * 1024 * 1024;
    if (file.Length > maxBytes)
        return Results.BadRequest(new { message = "File exceeds the 25 MB limit." });

    var attachmentId = Guid.NewGuid().ToString("N");
    var physicalPath = Path.Combine(secureUploadsRoot, $"{sensorId}_{attachmentId}.enc");

    await using (var uploadStream = file.OpenReadStream())
    {
        await encryption.EncryptToFileAsync(uploadStream, physicalPath);
    }

    var attachment = new AttachedFile
    {
        Id = attachmentId,
        FileName = Path.GetFileName(file.FileName),
        ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
        SizeBytes = file.Length,
        StoredPath = $"/api/sensors/{sensorId}/attachments/{attachmentId}/download",
        PhysicalPath = physicalPath
    };
    store.AttachFile(sensorId, attachment);

    return Results.Ok(attachment);
});

app.MapGet("/api/sensors/{sensorId}/attachments/{attachmentId}/download", async (
    string sensorId, string attachmentId, TelemetryStore store, FileEncryptionService encryption) =>
{
    var sensor = store.FindSensor(sensorId);
    var attachment = sensor?.Attachments.FirstOrDefault(a => a.Id == attachmentId);
    if (sensor is null || attachment is null || !File.Exists(attachment.PhysicalPath))
        return Results.NotFound(new { message = "Attachment not found." });

    var decrypted = await encryption.DecryptFromFileAsync(attachment.PhysicalPath);
    return Results.File(decrypted, attachment.ContentType, attachment.FileName);
});

app.Run();

// ---------------------------------------------------------------------------
// Request DTOs
// ---------------------------------------------------------------------------
record SensorRegistrationRequest(string MacAddress, string Location, SensorCategory Category, string DeploymentPath, string? Name = null);

record TelemetryIngestRequest<T>(string DeviceId, T Value) where T : struct;

record BatchItem(string DeviceId, double Value, string? Type);

record CommandRequest(string Target, string Action);

record InsightEvent(string Kind, string? Text);
