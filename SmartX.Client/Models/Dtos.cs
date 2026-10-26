namespace SmartX.Client.Models;

// Client-side mirrors of the API's models. Kept deliberately independent of the SmartX.Api
// project (no project reference) so the dashboard is a genuine HTTP client of the API.

public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
}

public class SensorProfileDto
{
    public string Id { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Location { get; set; } = "";
    public SensorCategory Category { get; set; }
    public string DeploymentPath { get; set; } = "";
    public DateTimeOffset RegisteredAt { get; set; }
    public List<AttachedFileDto> Attachments { get; set; } = new();
    public List<double> RecentValues { get; set; } = new();
    public bool IsValidated { get; set; }
}

public class AttachedFileDto
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoredPath { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
}

public class SensorReadingDto
{
    public string DeviceId { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
}

public class DeploymentNodeDto
{
    public string Name { get; set; } = "";
    public List<DeploymentNodeDto> Children { get; set; } = new();
}

public record SensorRegistrationRequest(string MacAddress, string Location, SensorCategory Category, string DeploymentPath, string? Name = null);

public record TelemetryIngestRequest<T>(string DeviceId, T Value);

// ------------------------------- Part 2 DTOs -------------------------------

public record BatchItemDto(string DeviceId, double Value, string Type);

public class DeviceViewDto
{
    public string Mac { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Location { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; }
    public int SecondsSinceSeen { get; set; }
    public bool IsActuator { get; set; }
    public bool IsOn { get; set; }
    public long PacketCount { get; set; }
    public List<double> Spark { get; set; } = new();
}

public class HistoryPointDto
{
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
}

public class ProcessedEntryDto
{
    public long Sequence { get; set; }
    public string DeviceName { get; set; } = "";
    public double Value { get; set; }
    public string Lane { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public class StreamStatsDto
{
    public int FifoDepth { get; set; }
    public int PriorityDepth { get; set; }
    public long Received { get; set; }
    public long Processed { get; set; }
    public long CriticalProcessed { get; set; }
    public long DuplicatesSuppressed { get; set; }
    public int ActiveErrorCount { get; set; }
    public int DisconnectedCount { get; set; }
    public int RegisteredDevices { get; set; }
    public int CommandStackDepth { get; set; }
    public int ReadingLogCount { get; set; }
    public int ReadingLogCapacity { get; set; }
    public List<ProcessedEntryDto> RecentProcessed { get; set; } = new();
}

public class ErrorStateDto
{
    public string Key { get; set; } = "";
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Level { get; set; } = "";
}

public class DisconnectedDto
{
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public class AlertRecordDto
{
    public long Id { get; set; }
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public double Value { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public string Lane { get; set; } = "";
    public int BypassedQueueDepth { get; set; }
}

public class AlertsViewDto
{
    public List<ErrorStateDto> ActiveErrors { get; set; } = new();
    public List<DisconnectedDto> Disconnected { get; set; } = new();
    public List<AlertRecordDto> Log { get; set; } = new();
    public long DuplicatesSuppressed { get; set; }
}

public class CommandChangeDto
{
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public double Before { get; set; }
    public double After { get; set; }
}

public class CommandRecordDto
{
    public long Id { get; set; }
    public string Target { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public List<CommandChangeDto> Changes { get; set; } = new();
}

public class InsightDto
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Confidence { get; set; }
    public string Severity { get; set; } = "Info";
    public string? TargetMac { get; set; }
    public string? Action { get; set; }
    public string? SearchText { get; set; }
}

public class InsightsViewDto
{
    public List<InsightDto> Insights { get; set; } = new();
    public List<string> RecentEvents { get; set; } = new();
    public int EventsRecorded { get; set; }
}

public class DisconnectResultDto
{
    public bool Raised { get; set; }
    public long DuplicatesSuppressedTotal { get; set; }
    public string Message { get; set; } = "";
}
