namespace SmartX.Api.Models;

/// <summary>
/// A registered Smart-X sensor / device record, created via the dashboard's
/// "Sensor Payload Management" screen.
/// </summary>
public class SensorProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Device MAC address or other unique hardware identifier.</summary>
    public required string MacAddress { get; set; }

    /// <summary>Human-readable deployment location, e.g. "Sub-Zone B".</summary>
    public required string Location { get; set; }

    public required SensorCategory Category { get; set; }

    /// <summary>
    /// Path into the deployment tree, e.g. "Facility A/Zone 1/Sub-Zone B". Validated
    /// recursively by <see cref="Services.RecursiveDeploymentValidator"/>.
    /// </summary>
    public required string DeploymentPath { get; set; }

    /// <summary>Optional friendly name (e.g. "Pump A"). Used by search and the action engine.</summary>
    public string Name { get; set; } = "";

    /// <summary>Friendly name, or a generated one when none was supplied.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name)
            ? Name
            : $"{Category} {(MacAddress.Length >= 5 ? MacAddress[^5..] : MacAddress)}";

    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Uploaded config files / deployment photos / hardware logs for this sensor.</summary>
    public List<AttachedFile> Attachments { get; init; } = new();

    /// <summary>Rolling recent readings for the pulse-grid widget (custom ring buffer).</summary>
    public TelemetryRingBuffer<double> RecentValues { get; init; } = new(capacity: 30);

    public bool IsValidated { get; set; }
}

public class AttachedFile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>API download URL exposed to clients (the file is served, never linked directly).</summary>
    public required string StoredPath { get; set; }

    /// <summary>Server-only location of the AES-encrypted file. Never sent to clients.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PhysicalPath { get; set; } = "";

    public DateTimeOffset UploadedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// RECURSION (data side): one node of the multi-tier deployment tree,
/// e.g. Facility A -> Zone 1 -> Sub-Zone B.
/// </summary>
public class DeploymentNode
{
    public required string Name { get; set; }
    public List<DeploymentNode> Children { get; init; } = new();

    public DeploymentNode? FindChild(string name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
