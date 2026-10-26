namespace SmartX.Api.Models;

/// <summary>
/// OPERATOR OVERLOADING REQUIREMENT.
///
/// SensorReading is a lightweight, immutable value used for numeric telemetry
/// (e.g. a smart meter's wattage, a tank's litres-per-minute flow). Overloading
/// +, -, ==, != and the comparison operators lets application/business code read
/// like the domain it models, e.g.:
///
///   var combinedLoad = meter1 + meter2;      // aggregate load of two smart meters
///   var delta        = readingNow - readingPrevious;  // change since last poll
///   if (combinedLoad > threshold) { ... }    // direct comparison, no .Value access
///
/// instead of manually unwrapping .Value on every call site.
/// </summary>
public readonly struct SensorReading : IEquatable<SensorReading>, IComparable<SensorReading>
{
    public string DeviceId { get; }
    public double Value { get; }
    public string Unit { get; }
    public DateTimeOffset CapturedAt { get; }

    public SensorReading(string deviceId, double value, string unit, DateTimeOffset? capturedAt = null)
    {
        DeviceId = deviceId;
        Value = value;
        Unit = unit;
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Aggregate two readings, e.g. Meter3 = Meter1 + Meter2.</summary>
    public static SensorReading operator +(SensorReading a, SensorReading b)
        => new($"{a.DeviceId}+{b.DeviceId}", a.Value + b.Value, a.Unit);

    /// <summary>Delta between two readings, e.g. usage this interval = now - previous.</summary>
    public static SensorReading operator -(SensorReading a, SensorReading b)
        => new($"{a.DeviceId}-{b.DeviceId}", a.Value - b.Value, a.Unit);

    public static bool operator ==(SensorReading a, SensorReading b) => a.Equals(b);
    public static bool operator !=(SensorReading a, SensorReading b) => !a.Equals(b);

    public static bool operator >(SensorReading a, SensorReading b) => a.Value > b.Value;
    public static bool operator <(SensorReading a, SensorReading b) => a.Value < b.Value;
    public static bool operator >=(SensorReading a, SensorReading b) => a.Value >= b.Value;
    public static bool operator <=(SensorReading a, SensorReading b) => a.Value <= b.Value;

    public bool Equals(SensorReading other) =>
        DeviceId == other.DeviceId && Value.Equals(other.Value) && Unit == other.Unit;

    public override bool Equals(object? obj) => obj is SensorReading other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DeviceId, Value, Unit);

    public int CompareTo(SensorReading other) => Value.CompareTo(other.Value);

    public override string ToString() => $"{DeviceId}: {Value:F2}{Unit} @ {CapturedAt:HH:mm:ss}";
}
