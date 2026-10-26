namespace SmartX.Api.Models;

/// <summary>
/// The sensor categories a Smart-X node can report. Kept as an enum (not a string)
/// so the ingestion pipeline never pays a parsing/boxing cost to classify a packet.
/// </summary>
public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
}

/// <summary>
/// The physical/logical value kind carried by a packet. Recorded explicitly so the
/// API can tell callers what <see cref="TelemetryPacket{T}.Value"/> actually holds
/// without runtime type inspection (reflection) on the hot ingestion path.
/// </summary>
public enum ReadingType
{
    Float,
    Integer,
    Boolean
}

/// <summary>
/// GENERICS REQUIREMENT.
///
/// TelemetryPacket&lt;T&gt; is the single reusable envelope every ESP32 node uses to publish
/// data back to the gateway, regardless of whether the underlying reading is a
/// float (soil moisture), an int (power wattage) or a bool (valve state).
///
/// Why generics instead of "object Value" or a DTO-per-type:
///   - A generic struct/class is specialised by the JIT for each closed type
///     (TelemetryPacket&lt;float&gt;, TelemetryPacket&lt;int&gt;, TelemetryPacket&lt;bool&gt;), so a
///     value-type T (float, int, bool) is stored inline - no boxing onto the
///     managed heap and no unboxing cast when it's read back. With thousands of
///     ESP32 nodes publishing constantly, avoiding a heap allocation per reading
///     is the difference between a gateway that scales and one that thrashes GC.
///   - "where T : struct" constrains T to value types, which is exactly the set
///     the assignment specifies (float / int / bool), and rules out accidentally
///     wrapping a reference type that would defeat the whole point of the class.
/// </summary>
/// <typeparam name="T">A value-type reading, e.g. float, int or bool.</typeparam>
public class TelemetryPacket<T> where T : struct
{
    public Guid PacketId { get; init; } = Guid.NewGuid();
    public required string DeviceId { get; init; }
    public required ReadingType Type { get; init; }
    public required T Value { get; set; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True once this reading has passed <see cref="Services.RecursiveDeploymentValidator"/>
    /// against its device's deployment-tree location.
    /// </summary>
    public bool IsValidated { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss}] {DeviceId} ({Type}) = {Value}";
}

/// <summary>
/// Non-generic helper so the API layer can build/describe a packet of the right
/// closed generic type from a runtime-known ReadingType (e.g. when deserialising
/// an incoming JSON payload) without the caller having to know T up front.
/// </summary>
public static class TelemetryPacketFactory
{
    public static object Create(string deviceId, ReadingType type, string rawValue) => type switch
    {
        ReadingType.Float => new TelemetryPacket<float>
        {
            DeviceId = deviceId,
            Type = type,
            Value = float.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture)
        },
        ReadingType.Integer => new TelemetryPacket<int>
        {
            DeviceId = deviceId,
            Type = type,
            Value = int.Parse(rawValue, System.Globalization.CultureInfo.InvariantCulture)
        },
        ReadingType.Boolean => new TelemetryPacket<bool>
        {
            DeviceId = deviceId,
            Type = type,
            Value = bool.Parse(rawValue)
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
}
