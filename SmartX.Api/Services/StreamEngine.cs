using SmartX.Api.Models;

namespace SmartX.Api.Services;

/// <summary>
/// PART 2 - REAL-TIME PROCESSING ENGINE.
///
/// Data structures used (and why):
///   Queue&lt;T&gt;                       _fifo           standard telemetry, First-In-First-Out
///   PriorityQueue&lt;T,(int,long)&gt;    _critical       critical alerts bypass the FIFO queue
///   Stack&lt;T&gt;                       _commandStack   manual override history -> "Undo"
///   Dictionary&lt;string,...&gt;         _live           live device state, O(1) lookup by MAC
///   SortedDictionary&lt;DateTimeOffset,...&gt; _history  per-device log ordered by timestamp
///   HashSet&lt;string&gt;                _activeErrors / _disconnected   unique error states, no duplicate alerts
///   TelemetryRingBuffer&lt;T&gt;         _alertLog / _processedLog      bounded logs (custom collection)
///
/// All shared state is guarded by one lock (_gate); every public method takes it.
/// </summary>
public class StreamEngine
{
    private const int MaxHistoryPerDevice = 600;
    private const string AllActuators = "ALL_ACTUATORS";

    private readonly object _gate = new();
    private readonly TelemetryStore _store;
    private readonly ActionEngine _actions;

    private readonly Queue<TelemetryEnvelope> _fifo = new();
    private readonly PriorityQueue<TelemetryEnvelope, (int Priority, long Sequence)> _critical = new();

    private readonly Dictionary<string, DeviceLiveState> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedDictionary<DateTimeOffset, SensorReading>> _history =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _activeErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disconnected = new(StringComparer.OrdinalIgnoreCase);

    private readonly Stack<CommandRecord> _commandStack = new();

    private readonly TelemetryRingBuffer<AlertRecord> _alertLog = new(200);
    private readonly TelemetryRingBuffer<ProcessedEntry> _processedLog = new(300);

    private long _sequence;
    private long _received;
    private long _processed;
    private long _criticalProcessed;
    private long _duplicatesSuppressed;
    private long _alertId;
    private long _commandId;

    public StreamEngine(TelemetryStore store, ActionEngine actions)
    {
        _store = store;
        _actions = actions;
        lock (_gate)
        {
            BackfillFromStore();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Classification (thresholds per sensor category)
    // ------------------------------------------------------------------------------------------

    public static (DeviceStatus Status, string Message) Classify(SensorCategory category, double value)
    {
        switch (category)
        {
            case SensorCategory.Environmental:
                if (value < 20) return (DeviceStatus.Critical, $"Moisture critically low ({value:F1}%)");
                if (value > 100) return (DeviceStatus.Critical, $"Moisture reading out of range ({value:F1}%)");
                if (value < 30) return (DeviceStatus.Warning, $"Moisture dropping ({value:F1}%)");
                if (value > 80) return (DeviceStatus.Warning, $"Moisture unusually high ({value:F1}%)");
                break;

            case SensorCategory.PowerConsumption:
                if (value > 500) return (DeviceStatus.Critical, $"Severe power spike ({value:F0} W)");
                if (value > 350) return (DeviceStatus.Warning, $"Power draw elevated ({value:F0} W)");
                break;
        }

        return (DeviceStatus.Normal, "Within expected range");
    }

    /// <summary>How far past the critical threshold a value is; larger = more urgent.</summary>
    private static int UrgencyScore(SensorCategory category, double value)
    {
        double score = category switch
        {
            SensorCategory.PowerConsumption => value - 500,
            SensorCategory.Environmental => value < 20 ? (20 - value) * 10 : (value - 100) * 10,
            _ => 0
        };

        return (int)Math.Clamp(score, 0, 1000);
    }

    // ------------------------------------------------------------------------------------------
    // Ingestion: packets are queued (not processed inline) so the API stays responsive.
    // The overloads keep TelemetryPacket<T> generic AND avoid boxing the value type.
    // ------------------------------------------------------------------------------------------

    public SubmitResult Submit(TelemetryPacket<float> packet) =>
        Submit(packet.DeviceId, packet.Value, ReadingType.Float);

    public SubmitResult Submit(TelemetryPacket<int> packet) =>
        Submit(packet.DeviceId, packet.Value, ReadingType.Integer);

    public SubmitResult Submit(TelemetryPacket<bool> packet) =>
        Submit(packet.DeviceId, packet.Value ? 1 : 0, ReadingType.Boolean);

    public SubmitResult Submit(string deviceId, double value, ReadingType type)
    {
        lock (_gate)
        {
            _sequence++;
            _received++;

            var sensor = _store.FindByMac(deviceId);
            var critical = false;
            var priority = 0;

            if (sensor is not null)
            {
                var (status, _) = Classify(sensor.Category, value);
                critical = status == DeviceStatus.Critical;
                if (critical)
                {
                    // Smaller number = served first. Worse readings get a smaller number.
                    priority = -UrgencyScore(sensor.Category, value);
                }
            }

            var envelope = new TelemetryEnvelope(_sequence, deviceId, value, type, DateTimeOffset.UtcNow, critical, priority);

            if (critical)
                _critical.Enqueue(envelope, (priority, _sequence));
            else
                _fifo.Enqueue(envelope);

            return new SubmitResult(_sequence, critical, _fifo.Count, _critical.Count);
        }
    }

    /// <summary>Drains up to <paramref name="max"/> packets. Critical packets are always served first.</summary>
    public int ProcessPending(int max)
    {
        var processed = 0;

        lock (_gate)
        {
            while (processed < max)
            {
                TelemetryEnvelope envelope;
                string lane;

                if (_critical.TryDequeue(out var criticalEnvelope, out _))
                {
                    envelope = criticalEnvelope;
                    lane = "PRIORITY";
                }
                else if (_fifo.Count > 0)
                {
                    envelope = _fifo.Dequeue();
                    lane = "FIFO";
                }
                else
                {
                    break;
                }

                ProcessOne(envelope, lane);
                processed++;
            }
        }

        return processed;
    }

    private void ProcessOne(TelemetryEnvelope envelope, string lane)
    {
        _processed++;

        var sensor = _store.FindByMac(envelope.DeviceId);
        var unit = envelope.Type == ReadingType.Boolean ? "state" : "unit";
        var reading = new SensorReading(envelope.DeviceId, envelope.Value, unit, envelope.ReceivedAt);

        _store.RecordReading(envelope.DeviceId, reading);
        AddHistory(envelope.DeviceId, reading);

        var name = sensor?.DisplayName ?? envelope.DeviceId;

        _processedLog.Add(new ProcessedEntry
        {
            Sequence = envelope.Sequence,
            DeviceName = name,
            Value = envelope.Value,
            Lane = lane,
            ProcessedAt = DateTimeOffset.UtcNow
        });

        if (sensor is null) return; // unregistered device: logged only

        var state = GetOrCreateLive(sensor);
        state.LastValue = envelope.Value;
        state.LastSeen = envelope.ReceivedAt;
        state.HasLivePacket = true;
        state.PacketCount++;

        // A packet from a node that was marked disconnected means it is back online.
        if (_disconnected.Remove(sensor.MacAddress))
        {
            AddAlert(sensor, "RECONNECTED", "Node is sending telemetry again", envelope.Value, "PRIORITY", 0);
        }

        var (status, message) = Classify(sensor.Category, envelope.Value);
        state.Status = status;
        state.StatusMessage = message;

        var criticalKey = sensor.MacAddress + "|Critical";
        var warningKey = sensor.MacAddress + "|Warning";

        if (status == DeviceStatus.Critical)
        {
            _criticalProcessed++;
            _activeErrors.Remove(warningKey);

            if (_activeErrors.Add(criticalKey))
            {
                AddAlert(sensor, "CRITICAL", message, envelope.Value, lane, lane == "PRIORITY" ? _fifo.Count : 0);
                _actions.Record("alert:" + sensor.DisplayName);
            }
            else
            {
                _duplicatesSuppressed++; // HashSet.Add returned false: alert already active
            }
        }
        else if (status == DeviceStatus.Warning)
        {
            _activeErrors.Remove(criticalKey);

            if (_activeErrors.Add(warningKey))
            {
                AddAlert(sensor, "WARNING", message, envelope.Value, lane, 0);
                _actions.Record("alert:" + sensor.DisplayName);
            }
            else
            {
                _duplicatesSuppressed++;
            }
        }
        else
        {
            // Back to normal: the error states are resolved and may fire again later.
            _activeErrors.Remove(criticalKey);
            _activeErrors.Remove(warningKey);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Disconnect detection (HashSet prevents duplicate disconnect alerts)
    // ------------------------------------------------------------------------------------------

    /// <summary>Marks devices that stopped sending packets as disconnected (once each).</summary>
    public int RunWatchdog(TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        var raised = 0;

        lock (_gate)
        {
            foreach (var state in _live.Values)
            {
                if (!state.HasLivePacket) continue;
                if (_disconnected.Contains(state.Mac)) continue;

                if (now - state.LastSeen > timeout)
                {
                    if (MarkDisconnected(state, "Watchdog: no telemetry received", "WATCHDOG"))
                        raised++;
                }
            }
        }

        return raised;
    }

    /// <summary>A device (or gateway "last will" message) reports that a node is offline.</summary>
    public (bool Found, bool Raised, long TotalSuppressed) ReportDisconnect(string mac)
    {
        lock (_gate)
        {
            var sensor = _store.FindByMac(mac);
            if (sensor is null) return (false, false, _duplicatesSuppressed);

            var state = GetOrCreateLive(sensor);
            var raised = MarkDisconnected(state, "Device reported disconnect", "DEVICE-REPORT");
            return (true, raised, _duplicatesSuppressed);
        }
    }

    private bool MarkDisconnected(DeviceLiveState state, string reason, string lane)
    {
        // HashSet<T>.Add returns false when the node is already in the set -> duplicate suppressed.
        if (!_disconnected.Add(state.Mac))
        {
            _duplicatesSuppressed++;
            return false;
        }

        state.Status = DeviceStatus.Disconnected;
        state.StatusMessage = reason;
        AddAlert(state.Sensor, "DISCONNECTED", reason, state.LastValue, lane, 0);
        _actions.Record("alert:" + state.Sensor.DisplayName);
        return true;
    }

    // ------------------------------------------------------------------------------------------
    // Manual override commands with a Stack<T> for Undo
    // ------------------------------------------------------------------------------------------

    public (CommandRecord? Record, string? Error) IssueCommand(string target, string action)
    {
        var normalised = NormaliseAction(action);
        if (normalised is null)
            return (null, "Action must be On, Off or Toggle.");

        lock (_gate)
        {
            EnsureDevices();

            var targets = new List<DeviceLiveState>();
            string label;

            if (string.Equals(target, AllActuators, StringComparison.OrdinalIgnoreCase))
            {
                targets.AddRange(_live.Values.Where(s => s.Sensor.Category == SensorCategory.Actuator));
                label = "All actuators";
                if (targets.Count == 0) return (null, "No actuators are registered.");
            }
            else
            {
                if (!_live.TryGetValue(target ?? "", out var single))
                    return (null, $"Unknown device '{target}'.");
                if (single.Sensor.Category != SensorCategory.Actuator)
                    return (null, $"{single.Sensor.DisplayName} is not an actuator and cannot be commanded.");

                targets.Add(single);
                label = single.Sensor.DisplayName;
            }

            _commandId++;
            var record = new CommandRecord
            {
                Id = _commandId,
                Target = target ?? "",
                TargetLabel = label,
                Action = normalised,
                IssuedAt = DateTimeOffset.UtcNow
            };

            foreach (var state in targets)
            {
                var before = state.LastValue >= 0.5 ? 1.0 : 0.0;
                var after = normalised switch
                {
                    "On" => 1.0,
                    "Off" => 0.0,
                    _ => before >= 0.5 ? 0.0 : 1.0
                };

                record.Changes.Add(new CommandChange
                {
                    Mac = state.Mac,
                    DeviceName = state.Sensor.DisplayName,
                    Before = before,
                    After = after
                });

                ApplyActuatorState(state, after);
            }

            _commandStack.Push(record);
            _actions.Record($"cmd:{label}:{normalised}");
            return (record, null);
        }
    }

    /// <summary>Pops the most recent command and restores every device it changed.</summary>
    public (CommandRecord? Record, string? Error) UndoLast()
    {
        lock (_gate)
        {
            if (_commandStack.Count == 0)
                return (null, "There is no command to undo.");

            var record = _commandStack.Pop();

            foreach (var change in record.Changes)
            {
                if (_live.TryGetValue(change.Mac, out var state))
                    ApplyActuatorState(state, change.Before);
            }

            return (record, null);
        }
    }

    /// <summary>Command history, most recent first (Stack&lt;T&gt; enumerates in LIFO order).</summary>
    public List<CommandRecord> CommandHistory()
    {
        lock (_gate) return _commandStack.ToList();
    }

    private void ApplyActuatorState(DeviceLiveState state, double newValue)
    {
        var now = DateTimeOffset.UtcNow;
        state.LastValue = newValue;

        // Server-originated state change: logged straight into the history so the timeline shows it.
        // (It is not a device heartbeat, so it does not reset the disconnect watchdog.)
        var reading = new SensorReading(state.Mac, newValue, "state", now);
        _store.RecordReading(state.Mac, reading);
        AddHistory(state.Mac, reading);
    }

    private static string? NormaliseAction(string? action)
    {
        if (string.Equals(action, "on", StringComparison.OrdinalIgnoreCase)) return "On";
        if (string.Equals(action, "off", StringComparison.OrdinalIgnoreCase)) return "Off";
        if (string.Equals(action, "toggle", StringComparison.OrdinalIgnoreCase)) return "Toggle";
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Queries for the dashboard
    // ------------------------------------------------------------------------------------------

    public List<DeviceView> QueryDevices(string? text, string? category, string? state)
    {
        lock (_gate)
        {
            EnsureDevices();

            IEnumerable<DeviceLiveState> source = _live.Values;

            if (!string.IsNullOrWhiteSpace(text))
            {
                var needle = text.Trim();
                source = source.Where(s =>
                    s.Sensor.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.Mac.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.Sensor.Location.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.Sensor.DeploymentPath.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("All", StringComparison.OrdinalIgnoreCase))
                source = source.Where(s => s.Sensor.Category.ToString().Equals(category, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(state) && !state.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                if (state.Equals("Alert", StringComparison.OrdinalIgnoreCase))
                {
                    source = source.Where(s => s.Status is DeviceStatus.Warning or DeviceStatus.Critical or DeviceStatus.Disconnected);
                }
                else
                {
                    source = source.Where(s => s.Status.ToString().Equals(state, StringComparison.OrdinalIgnoreCase));
                }
            }

            return source
                .OrderByDescending(s => SeverityRank(s.Status))
                .ThenBy(s => s.Sensor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(ToView)
                .ToList();
        }
    }

    public List<HistoryPoint> History(string mac, int take)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(mac, out var history)) return new List<HistoryPoint>();

            // SortedDictionary enumerates in key (timestamp) order, so the timeline is already sorted.
            return history
                .TakeLast(Math.Clamp(take, 1, MaxHistoryPerDevice))
                .Select(pair => new HistoryPoint { Timestamp = pair.Key, Value = pair.Value.Value })
                .ToList();
        }
    }

    public AlertsView GetAlerts()
    {
        lock (_gate)
        {
            var view = new AlertsView { DuplicatesSuppressed = _duplicatesSuppressed };

            foreach (var key in _activeErrors.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var parts = key.Split('|');
                var mac = parts[0];
                var sensor = _store.FindByMac(mac);

                view.ActiveErrors.Add(new ErrorStateView
                {
                    Key = key,
                    Mac = mac,
                    DeviceName = sensor?.DisplayName ?? mac,
                    Level = parts.Length > 1 ? parts[1] : ""
                });
            }

            foreach (var mac in _disconnected.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
            {
                var sensor = _store.FindByMac(mac);
                view.Disconnected.Add(new DisconnectedView { Mac = mac, DeviceName = sensor?.DisplayName ?? mac });
            }

            view.Log = _alertLog.Reverse().Take(30).ToList();
            return view;
        }
    }

    public StreamStats GetStats()
    {
        lock (_gate)
        {
            var (capacity, count) = _store.ReadingLogBufferStatus;

            return new StreamStats
            {
                FifoDepth = _fifo.Count,
                PriorityDepth = _critical.Count,
                Received = _received,
                Processed = _processed,
                CriticalProcessed = _criticalProcessed,
                DuplicatesSuppressed = _duplicatesSuppressed,
                ActiveErrorCount = _activeErrors.Count,
                DisconnectedCount = _disconnected.Count,
                RegisteredDevices = _live.Count,
                CommandStackDepth = _commandStack.Count,
                ReadingLogCount = count,
                ReadingLogCapacity = capacity,
                RecentProcessed = _processedLog.Reverse().Take(200).ToList()
            };
        }
    }

    // ------------------------------------------------------------------------------------------
    // Automated insights: problem devices + trend projection + predicted operator actions
    // ------------------------------------------------------------------------------------------

    public InsightsView GetInsights()
    {
        lock (_gate)
        {
            EnsureDevices();

            // PriorityQueue orders the cards: smallest priority number is served first, so scores are negated.
            var ranked = new PriorityQueue<Insight, double>();

            // 1. Devices that are currently faulty or out of range.
            foreach (var state in _live.Values)
            {
                if (state.Status is DeviceStatus.Normal or DeviceStatus.Unknown) continue;

                var rank = SeverityRank(state.Status);
                var insight = new Insight
                {
                    Kind = "Problem device",
                    Title = $"{state.Sensor.DisplayName} needs attention",
                    Detail = $"{state.StatusMessage}. Located at {state.Sensor.DeploymentPath}.",
                    Confidence = 1.0,
                    Severity = state.Status.ToString(),
                    TargetMac = state.Mac,
                    SearchText = state.Sensor.DisplayName
                };
                ranked.Enqueue(insight, -(100.0 * rank));
            }

            // 2. Trend projection: readings moving steadily toward a threshold.
            foreach (var state in _live.Values)
            {
                if (state.Sensor.Category == SensorCategory.Actuator) continue;
                if (state.Status != DeviceStatus.Normal) continue;
                if (!_history.TryGetValue(state.Mac, out var history)) continue;

                var recent = history.Values.TakeLast(6).Select(r => r.Value).ToList();
                var trend = ProjectBreach(state.Sensor.Category, recent);
                if (trend is null) continue;

                ranked.Enqueue(new Insight
                {
                    Kind = "Trend warning",
                    Title = $"{state.Sensor.DisplayName} is trending toward a limit",
                    Detail = trend,
                    Confidence = 0.7,
                    Severity = "Warning",
                    TargetMac = state.Mac,
                    SearchText = state.Sensor.DisplayName
                }, -60.0);
            }

            // 3. What operators usually do next, given the alerts that are active right now.
            var context = new List<string>();
            foreach (var state in _live.Values)
            {
                if (state.Status is DeviceStatus.Warning or DeviceStatus.Critical or DeviceStatus.Disconnected)
                    context.Add("alert:" + state.Sensor.DisplayName);
            }

            var lastToken = _actions.LastToken;
            if (lastToken is not null) context.Add(lastToken);

            foreach (var prediction in _actions.Predict(context, 5))
            {
                var insight = BuildPredictionInsight(prediction);
                if (insight is not null)
                    ranked.Enqueue(insight, -(50.0 + 40.0 * prediction.Confidence));
            }

            var result = new InsightsView
            {
                RecentEvents = _actions.RecentEvents(),
                EventsRecorded = _actions.EventsRecorded
            };

            while (ranked.Count > 0 && result.Insights.Count < 8)
                result.Insights.Add(ranked.Dequeue());

            return result;
        }
    }

    private Insight? BuildPredictionInsight(Prediction prediction)
    {
        var because = prediction.Because.StartsWith("alert:", StringComparison.OrdinalIgnoreCase)
            ? $"after an alert on {prediction.Because.Substring(6)}"
            : "after your last action";

        if (prediction.Token.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
        {
            var body = prediction.Token.Substring(4);
            var split = body.LastIndexOf(':');
            if (split <= 0) return null;

            var deviceName = body.Substring(0, split);
            var action = body.Substring(split + 1);
            var device = _live.Values.FirstOrDefault(s =>
                s.Sensor.DisplayName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
            if (device is null) return null;

            return new Insight
            {
                Kind = "Suggested action",
                Title = $"Run: {deviceName} {action}",
                Detail = $"Operators ran this {prediction.Count} times {because} ({prediction.Confidence:P0} of the time).",
                Confidence = prediction.Confidence,
                Severity = "Info",
                TargetMac = device.Mac,
                Action = action
            };
        }

        var text = prediction.Token.Substring(prediction.Token.IndexOf(':') + 1);
        return new Insight
        {
            Kind = "Predicted search",
            Title = $"You will probably look at \"{text}\" next",
            Detail = $"Searched {prediction.Count} times {because} ({prediction.Confidence:P0} of the time).",
            Confidence = prediction.Confidence,
            Severity = "Info",
            SearchText = text
        };
    }

    /// <summary>Straight-line projection of the last readings; returns text when a limit is close.</summary>
    private static string? ProjectBreach(SensorCategory category, List<double> values)
    {
        var n = values.Count;
        if (n < 6) return null;

        // Require a steady direction (at least 4 of the 5 steps in the same direction) and no jumps.
        var rising = 0;
        var falling = 0;
        var maxJump = 0.0;
        for (var i = 1; i < n; i++)
        {
            var delta = values[i] - values[i - 1];
            if (delta > 0) rising++;
            if (delta < 0) falling++;
            maxJump = Math.Max(maxJump, Math.Abs(delta));
        }

        if (maxJump > 15) return null;

        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (var i = 0; i < n; i++)
        {
            sumX += i;
            sumY += values[i];
            sumXY += i * values[i];
            sumXX += (double)i * i;
        }

        var denominator = n * sumXX - sumX * sumX;
        if (Math.Abs(denominator) < 1e-9) return null;

        var slope = (n * sumXY - sumX * sumY) / denominator;
        var current = values[n - 1];

        if (category == SensorCategory.Environmental)
        {
            if (slope < -0.5 && falling >= 4 && current > 30)
            {
                var steps = (current - 30) / -slope;
                if (steps <= 15)
                    return $"Moisture is falling about {-slope:F1} points per reading; it should cross the 30% warning line in roughly {Math.Ceiling(steps)} readings.";
            }

            if (slope > 0.5 && rising >= 4 && current < 80)
            {
                var steps = (80 - current) / slope;
                if (steps <= 15)
                    return $"Moisture is rising about {slope:F1} points per reading; it should cross the 80% warning line in roughly {Math.Ceiling(steps)} readings.";
            }
        }
        else if (category == SensorCategory.PowerConsumption)
        {
            if (slope > 5 && rising >= 4 && current < 350)
            {
                var steps = (350 - current) / slope;
                if (steps <= 15)
                    return $"Power draw is climbing about {slope:F0} W per reading; it should cross the 350 W warning line in roughly {Math.Ceiling(steps)} readings.";
            }
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Helpers (callers already hold _gate)
    // ------------------------------------------------------------------------------------------

    private void BackfillFromStore()
    {
        EnsureDevices();

        foreach (var reading in _store.RecentReadings(2000))
        {
            if (!_live.TryGetValue(reading.DeviceId, out var state)) continue;

            AddHistory(reading.DeviceId, reading);
            state.LastValue = reading.Value;
            state.LastSeen = reading.CapturedAt;
        }

        foreach (var state in _live.Values)
        {
            var (status, message) = Classify(state.Sensor.Category, state.LastValue);
            state.Status = status;
            state.StatusMessage = message;
        }
    }

    private void EnsureDevices()
    {
        foreach (var sensor in _store.Sensors)
        {
            if (!_live.ContainsKey(sensor.MacAddress))
                GetOrCreateLive(sensor);
        }
    }

    private DeviceLiveState GetOrCreateLive(SensorProfile sensor)
    {
        if (_live.TryGetValue(sensor.MacAddress, out var existing)) return existing;

        var state = new DeviceLiveState
        {
            Mac = sensor.MacAddress,
            Sensor = sensor,
            LastSeen = sensor.RegisteredAt
        };
        _live[sensor.MacAddress] = state;
        return state;
    }

    private void AddHistory(string mac, SensorReading reading)
    {
        if (!_history.TryGetValue(mac, out var history))
        {
            history = new SortedDictionary<DateTimeOffset, SensorReading>();
            _history[mac] = history;
        }

        // SortedDictionary keys must be unique: nudge identical timestamps by one tick.
        var key = reading.CapturedAt;
        while (history.ContainsKey(key))
            key = key.AddTicks(1);

        history[key] = reading;

        while (history.Count > MaxHistoryPerDevice)
        {
            var oldest = history.Keys.First();
            history.Remove(oldest);
        }
    }

    private void AddAlert(SensorProfile sensor, string kind, string message, double value, string lane, int bypassed)
    {
        _alertId++;
        _alertLog.Add(new AlertRecord
        {
            Id = _alertId,
            Mac = sensor.MacAddress,
            DeviceName = sensor.DisplayName,
            Kind = kind,
            Message = message,
            Value = value,
            RaisedAt = DateTimeOffset.UtcNow,
            Lane = lane,
            BypassedQueueDepth = bypassed
        });
    }

    private static int SeverityRank(DeviceStatus status) => status switch
    {
        DeviceStatus.Critical => 4,
        DeviceStatus.Disconnected => 3,
        DeviceStatus.Warning => 2,
        DeviceStatus.Unknown => 1,
        _ => 0
    };

    private DeviceView ToView(DeviceLiveState state)
    {
        var spark = new List<double>();
        if (_history.TryGetValue(state.Mac, out var history))
            spark = history.Values.TakeLast(24).Select(r => r.Value).ToList();

        var category = state.Sensor.Category;

        return new DeviceView
        {
            Mac = state.Mac,
            Name = state.Sensor.DisplayName,
            Category = category.ToString(),
            Location = state.Sensor.Location,
            DeploymentPath = state.Sensor.DeploymentPath,
            Value = state.LastValue,
            Unit = category switch
            {
                SensorCategory.Environmental => "%",
                SensorCategory.PowerConsumption => "W",
                _ => ""
            },
            Status = state.Status.ToString(),
            Message = state.StatusMessage,
            LastSeen = state.LastSeen,
            SecondsSinceSeen = (int)Math.Max(0, (DateTimeOffset.UtcNow - state.LastSeen).TotalSeconds),
            IsActuator = category == SensorCategory.Actuator,
            IsOn = category == SensorCategory.Actuator && state.LastValue >= 0.5,
            PacketCount = state.PacketCount,
            Spark = spark
        };
    }
}
