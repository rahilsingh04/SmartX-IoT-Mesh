using SmartX.Client.Models;

namespace SmartX.Client.Services;

/// <summary>
/// Simulated ESP32 fleet. Every tick it asks the API which devices exist and then POSTs one
/// batch of telemetry back to the API exactly like real devices would. Scoped (per circuit), so
/// it keeps running while the operator navigates between modules.
/// </summary>
public class SimulatorService : IDisposable
{
    private readonly SmartXApiClient _api;
    private readonly Random _rnd = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int Ticks, string Kind)> _anomalies = new(StringComparer.OrdinalIgnoreCase);

    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _disposed;

    public SimulatorService(SmartXApiClient api)
    {
        _api = api;
    }

    public bool IsRunning { get; private set; }
    public int TickCount { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised from a background thread; subscribers must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    public bool IsMuted(string mac)
    {
        lock (_gate) return _muted.Contains(mac);
    }

    public int MutedCount
    {
        get { lock (_gate) return _muted.Count; }
    }

    public void Start()
    {
        if (IsRunning || _disposed) return;

        IsRunning = true;
        _timer = new System.Threading.Timer(
            state => { _ = TickAsync(); },
            null,
            TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(1500));
        Changed?.Invoke();
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
        Changed?.Invoke();
    }

    public void Toggle()
    {
        if (IsRunning) Stop();
        else Start();
    }

    /// <summary>Silence one node so it stops sending packets (simulates a hardware dropout).</summary>
    public void ToggleMute(string mac)
    {
        lock (_gate)
        {
            if (!_muted.Add(mac))
                _muted.Remove(mac);
        }

        Changed?.Invoke();
    }

    private async Task TickAsync()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            var devices = await _api.QueryDevicesAsync(null, null, null);
            var items = new List<BatchItemDto>();

            foreach (var device in devices)
            {
                if (IsMuted(device.Mac)) continue;
                items.Add(NextPacket(device));
            }

            if (items.Count > 0)
                await _api.SendBatchAsync(items);

            TickCount++;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Changed?.Invoke();
        }
    }

    private BatchItemDto NextPacket(DeviceViewDto device)
    {
        lock (_gate)
        {
            switch (device.Category)
            {
                case "Environmental":
                {
                    var kind = CurrentAnomaly(device.Mac);
                    var value = 40 + _rnd.NextDouble() * 8;
                    if (kind == "critical") value = 8 + _rnd.NextDouble() * 10;      // critical moisture drop
                    else if (kind == "warning") value = 88 + _rnd.NextDouble() * 8;  // unusually wet
                    return new BatchItemDto(device.Mac, Math.Round(value, 1), "float");
                }

                case "PowerConsumption":
                {
                    var kind = CurrentAnomaly(device.Mac);
                    var value = 200 + _rnd.Next(0, 40);
                    if (kind == "critical") value = 560 + _rnd.Next(0, 160);         // severe power spike
                    else if (kind == "warning") value = 380 + _rnd.Next(0, 50);
                    return new BatchItemDto(device.Mac, value, "int");
                }

                default:
                    // Actuators simply report the state they are in (commands change it server side).
                    return new BatchItemDto(device.Mac, device.IsOn ? 1 : 0, "bool");
            }
        }
    }

    // Called with _gate held. Anomalies last four ticks so they are readable on screen.
    private string? CurrentAnomaly(string mac)
    {
        if (_anomalies.TryGetValue(mac, out var active))
        {
            if (active.Ticks <= 1) _anomalies.Remove(mac);
            else _anomalies[mac] = (active.Ticks - 1, active.Kind);
            return active.Kind;
        }

        if (_rnd.NextDouble() < 0.015)
        {
            var kind = _rnd.NextDouble() < 0.6 ? "critical" : "warning";
            _anomalies[mac] = (3, kind);
            return kind;
        }

        return null;
    }

    // ----------------------------------------------------------------------- manual test tools

    /// <summary>One critical packet, sent to the API like a faulty device would.</summary>
    public async Task<string> FireCriticalAsync(DeviceViewDto device)
    {
        if (device.Category == "Environmental")
        {
            await _api.IngestFloatAsync(device.Mac, 9.5f);
            return $"Sent critical moisture drop (9.5%) for {device.Name}.";
        }

        if (device.Category == "PowerConsumption")
        {
            await _api.IngestIntAsync(device.Mac, 950);
            return $"Sent severe power spike (950 W) for {device.Name}.";
        }

        return $"{device.Name} is an actuator: it has no critical range.";
    }

    /// <summary>
    /// Sends one batch of normal packets with a single critical packet at the very END. Watch the
    /// "Processing order" panel: the critical packet is processed first even though it arrived last.
    /// </summary>
    public async Task<string> SendBurstAsync(IReadOnlyList<DeviceViewDto> devices, int count)
    {
        var sensors = devices.Where(d => !d.IsActuator).ToList();
        if (sensors.Count == 0) return "No sensors are registered.";

        var items = new List<BatchItemDto>();
        lock (_gate)
        {
            for (var i = 0; i < count; i++)
            {
                var device = sensors[_rnd.Next(sensors.Count)];
                items.Add(device.Category == "PowerConsumption"
                    ? new BatchItemDto(device.Mac, 200 + _rnd.Next(0, 40), "int")
                    : new BatchItemDto(device.Mac, Math.Round(40 + _rnd.NextDouble() * 8, 1), "float"));
            }
        }

        var target = sensors.FirstOrDefault(d => d.Category == "PowerConsumption") ?? sensors[0];
        items.Add(target.Category == "PowerConsumption"
            ? new BatchItemDto(target.Mac, 950, "int")
            : new BatchItemDto(target.Mac, 9.5, "float"));

        var ok = await _api.SendBatchAsync(items);
        return ok
            ? $"Sent {count} normal packets + 1 critical packet (last in the batch) for {target.Name}."
            : "The API rejected the batch.";
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
