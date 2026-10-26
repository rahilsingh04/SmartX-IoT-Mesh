using SmartX.Api.Models;

namespace SmartX.Api.Services;

/// <summary>
/// In-memory registry of sensors plus the global reading log. A real deployment would swap the
/// backing collections for a database, but the data-structure work lives here regardless.
/// </summary>
public class TelemetryStore
{
    private readonly object _lock = new();
    private readonly List<SensorProfile> _sensors = new();

    // Part 2: Dictionary keyed by MAC address for instantaneous O(1) lookup when a packet arrives.
    private readonly Dictionary<string, SensorProfile> _byMac = new(StringComparer.OrdinalIgnoreCase);

    // Custom fixed-capacity collection (see TelemetryRingBuffer<T>): O(1) inserts, fixed memory.
    private readonly TelemetryRingBuffer<SensorReading> _readingLog = new(capacity: 2000);
    private readonly RecursiveDeploymentValidator _validator;

    // ADVANCED ARRAYS: raw telemetry arrives in ragged batches per polling cycle, so a jagged
    // array (float[][]) is used for the raw batches before they are flattened into the
    // optimised collection above.
    private float[][] _rawHistoricalBatches = Array.Empty<float[]>();

    public TelemetryStore()
    {
        _validator = new RecursiveDeploymentValidator(RecursiveDeploymentValidator.BuildDefaultFacilityTree());
        SeedMockData();
    }

    public DeploymentNode FacilityTree => RecursiveDeploymentValidator.BuildDefaultFacilityTree();

    public IReadOnlyList<SensorProfile> Sensors
    {
        get { lock (_lock) return _sensors.ToList(); }
    }

    /// <summary>O(1) Dictionary lookup of a registered device by MAC / unique id.</summary>
    public SensorProfile? FindByMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        lock (_lock) return _byMac.TryGetValue(mac, out var sensor) ? sensor : null;
    }

    public IReadOnlyList<SensorReading> RecentReadings(int take = 50)
    {
        lock (_lock) return _readingLog.TakeLast(take).ToList();
    }

    public (int capacity, int count) ReadingLogBufferStatus
    {
        get { lock (_lock) return (_readingLog.Capacity, _readingLog.Count); }
    }

    public (bool ok, string message) RegisterSensor(SensorProfile profile)
    {
        var valid = _validator.IsValidPath(profile.DeploymentPath, out var message);
        profile.IsValidated = valid;

        if (!valid) return (false, message);

        lock (_lock)
        {
            if (_byMac.ContainsKey(profile.MacAddress))
                return (false, $"A sensor with identifier '{profile.MacAddress}' is already registered.");

            _sensors.Add(profile);
            _byMac[profile.MacAddress] = profile;
        }

        return (true, message);
    }

    public SensorProfile? FindSensor(string id)
    {
        lock (_lock) return _sensors.FirstOrDefault(s => s.Id == id);
    }

    public void AttachFile(string sensorId, AttachedFile file)
    {
        lock (_lock)
        {
            var sensor = _sensors.FirstOrDefault(s => s.Id == sensorId);
            sensor?.Attachments.Add(file);
        }
    }

    /// <summary>Appends a reading to the global log and the sensor's rolling window.</summary>
    public void RecordReading(string deviceId, SensorReading reading)
    {
        lock (_lock)
        {
            _readingLog.Add(reading);
            if (_byMac.TryGetValue(deviceId, out var sensor))
                sensor.RecentValues.Add(reading.Value);
        }
    }

    /// <summary>
    /// Jagged-array -> optimised collection pipeline: ragged batches (rows of different length,
    /// as when nodes miss polling cycles) are flattened into the ring-buffer reading log.
    /// </summary>
    public void IngestHistoricalBatch(float[][] jaggedBatch)
    {
        lock (_lock)
        {
            _rawHistoricalBatches = Combine(_rawHistoricalBatches, jaggedBatch);

            for (int row = 0; row < jaggedBatch.Length; row++)
            {
                for (int col = 0; col < jaggedBatch[row].Length; col++)
                {
                    var deviceId = $"BATCH-{row:D2}";
                    _readingLog.Add(new SensorReading(deviceId, jaggedBatch[row][col], "raw"));
                }
            }
        }
    }

    private static float[][] Combine(float[][] existing, float[][] extra)
    {
        var combined = new float[existing.Length + extra.Length][];
        Array.Copy(existing, combined, existing.Length);
        Array.Copy(extra, 0, combined, existing.Length, extra.Length);
        return combined;
    }

    /// <summary>
    /// Seeds mock sensors + a jagged batch of historical telemetry so the pipeline and dashboard
    /// can be demonstrated without physical ESP32 hardware.
    /// </summary>
    private void SeedMockData()
    {
        var rnd = new Random(42);

        var seedSensors = new (string Mac, string Name, string Path, SensorCategory Category)[]
        {
            ("AA:BB:CC:00:00:01", "Moisture Sensor A", "Facility A/Zone 1/Sub-Zone A", SensorCategory.Environmental),
            ("AA:BB:CC:00:00:02", "Smart Meter 1",     "Facility A/Zone 1/Sub-Zone B", SensorCategory.PowerConsumption),
            ("AA:BB:CC:00:00:03", "Pump A",            "Facility A/Zone 1/Sub-Zone B", SensorCategory.Actuator),
            ("AA:BB:CC:00:00:04", "Moisture Sensor B", "Facility A/Zone 2/Sub-Zone C", SensorCategory.Environmental),
            ("AA:BB:CC:00:00:05", "Smart Meter 2",     "Facility A/Zone 2/Sub-Zone C", SensorCategory.PowerConsumption),
            ("AA:BB:CC:00:00:06", "Valve V1",          "Facility A/Zone 1/Sub-Zone A", SensorCategory.Actuator),
            ("AA:BB:CC:00:00:07", "Valve V2",          "Facility A/Zone 2/Sub-Zone C", SensorCategory.Actuator),
            ("AA:BB:CC:00:00:08", "Relay R1",          "Facility A/Zone 1/Sub-Zone B", SensorCategory.Actuator),
        };

        foreach (var (mac, name, path, category) in seedSensors)
        {
            var profile = new SensorProfile
            {
                MacAddress = mac,
                Name = name,
                Location = path.Split('/').Last(),
                Category = category,
                DeploymentPath = path
            };
            RegisterSensor(profile);

            for (int i = 0; i < 20; i++)
            {
                double baseValue = category switch
                {
                    SensorCategory.Environmental => 40 + rnd.NextDouble() * 10,
                    SensorCategory.PowerConsumption => 200 + rnd.NextDouble() * 50,
                    SensorCategory.Actuator => rnd.Next(0, 2),
                    _ => 0
                };

                // One historical spike so the pulse grid / history have something to show.
                if (i == 15 && category != SensorCategory.Actuator) baseValue *= 2.4;

                // Make sure every actuator's latest seeded state is a clean 0 or 1.
                if (i == 19 && category == SensorCategory.Actuator) baseValue = 0;

                RecordReading(mac, new SensorReading(mac, baseValue, "unit",
                    DateTimeOffset.UtcNow.AddSeconds(-1 * (20 - i) * 5)));
            }
        }

        // Ragged (jagged) historical batch: row lengths intentionally vary.
        var jagged = new float[][]
        {
            new float[] { 21.4f, 21.8f, 22.1f },
            new float[] { 220.5f, 219.9f },
            new float[] { 0f, 1f, 1f, 0f, 1f },
            new float[] { 44.2f }
        };
        IngestHistoricalBatch(jagged);
    }
}
