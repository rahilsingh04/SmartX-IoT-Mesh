namespace SmartX.Api.Models;

public enum DeviceStatus
{
    Unknown = 0,
    Normal = 1,
    Warning = 2,
    Critical = 3,
    Disconnected = 4
}

/// <summary>
/// One telemetry packet waiting in the processing pipeline. A readonly record struct keeps
/// the queues free of per-packet heap allocations (same reasoning as TelemetryPacket&lt;T&gt;).
/// </summary>
public readonly record struct TelemetryEnvelope(
    long Sequence,
    string DeviceId,
    double Value,
    ReadingType Type,
    DateTimeOffset ReceivedAt,
    bool IsCritical,
    int Priority);

/// <summary>What the API tells a device after it submits a packet.</summary>
public record SubmitResult(long Sequence, bool Critical, int FifoDepth, int PriorityDepth);

/// <summary>Live state of one device, stored in a Dictionary keyed by MAC for O(1) lookup.</summary>
public class DeviceLiveState
{
    public required string Mac { get; init; }
    public required SensorProfile Sensor { get; init; }
    public double LastValue { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Unknown;
    public string StatusMessage { get; set; } = "Awaiting first packet";

    /// <summary>True once a real (non-seed) packet has arrived; only then is the watchdog active.</summary>
    public bool HasLivePacket { get; set; }

    public long PacketCount { get; set; }
}

/// <summary>JSON-friendly snapshot of a device for the dashboard.</summary>
public class DeviceView
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

public class HistoryPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
}

public class AlertRecord
{
    public long Id { get; set; }
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Message { get; set; } = "";
    public double Value { get; set; }
    public DateTimeOffset RaisedAt { get; set; }

    /// <summary>"PRIORITY" (came through the priority queue), "WATCHDOG" or "DEVICE-REPORT".</summary>
    public string Lane { get; set; } = "";

    /// <summary>How many packets were waiting in the FIFO queue that this alert jumped ahead of.</summary>
    public int BypassedQueueDepth { get; set; }
}

public class ProcessedEntry
{
    public long Sequence { get; set; }
    public string DeviceName { get; set; } = "";
    public double Value { get; set; }
    public string Lane { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public class ErrorStateView
{
    public string Key { get; set; } = "";
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Level { get; set; } = "";
}

public class DisconnectedView
{
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
}

public class AlertsView
{
    public List<ErrorStateView> ActiveErrors { get; set; } = new();
    public List<DisconnectedView> Disconnected { get; set; } = new();
    public List<AlertRecord> Log { get; set; } = new();
    public long DuplicatesSuppressed { get; set; }
}

public class StreamStats
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
    public List<ProcessedEntry> RecentProcessed { get; set; } = new();
}

public class CommandChange
{
    public string Mac { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public double Before { get; set; }
    public double After { get; set; }
}

public class CommandRecord
{
    public long Id { get; set; }
    public string Target { get; set; } = "";
    public string TargetLabel { get; set; } = "";
    public string Action { get; set; } = "";
    public DateTimeOffset IssuedAt { get; set; }
    public List<CommandChange> Changes { get; set; } = new();
}

public class Insight
{
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double Confidence { get; set; }
    public string Severity { get; set; } = "Info";
    public string? TargetMac { get; set; }

    /// <summary>"On", "Off" or "Toggle" when the insight can be applied as a command.</summary>
    public string? Action { get; set; }

    /// <summary>Text to drop into the search box when the insight is a predicted search.</summary>
    public string? SearchText { get; set; }
}

public class InsightsView
{
    public List<Insight> Insights { get; set; } = new();
    public List<string> RecentEvents { get; set; } = new();
    public int EventsRecorded { get; set; }
}

public record Prediction(string Token, string Because, double Confidence, int Count);
