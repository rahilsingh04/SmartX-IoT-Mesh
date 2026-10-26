using SmartX.Api.Models;

namespace SmartX.Api.Services;

/// <summary>
/// PREDICTIVE ACTION ENGINE (pattern analysis).
///
/// Every alert, search and manual command is turned into a small text "token":
///     alert:Moisture Sensor B     search:pump a     cmd:Pump A:On
/// The engine keeps a first-order Markov chain over these tokens: for each token it counts
/// which token came NEXT (Dictionary&lt;string, Dictionary&lt;string,int&gt;&gt;). That is exactly the
/// pattern "operators search for Pump A right after Moisture Sensor B drops".
///
/// Predict() takes the tokens that describe the CURRENT situation (e.g. the alerts that are
/// active right now) and returns the most likely follow-up commands/searches with a
/// confidence = count / total transitions from that token. The dashboard shows these as
/// "Suggested Actions" before the operator has searched for anything.
/// </summary>
public class ActionEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, int>> _transitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recent = new();
    private string? _last;
    private int _events;

    public ActionEngine()
    {
        SeedHistoricalPatterns();
    }

    /// <summary>Records one event token and learns the transition from the previous token.</summary>
    public void Record(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;

        lock (_gate)
        {
            if (_last is not null)
                Bump(_last, token);

            _last = token;
            _events++;

            _recent.Enqueue(token);
            while (_recent.Count > 12)
                _recent.Dequeue();
        }
    }

    public string? LastToken
    {
        get { lock (_gate) return _last; }
    }

    public int EventsRecorded
    {
        get { lock (_gate) return _events; }
    }

    public List<string> RecentEvents()
    {
        lock (_gate)
        {
            var list = _recent.ToList();
            list.Reverse();
            return list;
        }
    }

    /// <summary>Most likely user actions given the context tokens (only cmd:/search: follow-ups).</summary>
    public List<Prediction> Predict(IEnumerable<string> contextTokens, int max)
    {
        lock (_gate)
        {
            var best = new Dictionary<string, Prediction>(StringComparer.OrdinalIgnoreCase);

            foreach (var ctx in contextTokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_transitions.TryGetValue(ctx, out var next)) continue;

                var total = next.Values.Sum();
                if (total == 0) continue;

                foreach (var pair in next)
                {
                    var isUserAction = pair.Key.StartsWith("cmd:", StringComparison.OrdinalIgnoreCase)
                                       || pair.Key.StartsWith("search:", StringComparison.OrdinalIgnoreCase);
                    if (!isUserAction || pair.Value < 2) continue;

                    var confidence = (double)pair.Value / total;
                    if (!best.TryGetValue(pair.Key, out var existing) || confidence > existing.Confidence)
                        best[pair.Key] = new Prediction(pair.Key, ctx, confidence, pair.Value);
                }
            }

            return best.Values
                .OrderByDescending(p => p.Confidence)
                .ThenByDescending(p => p.Count)
                .Take(max)
                .ToList();
        }
    }

    private void Bump(string from, string to)
    {
        if (!_transitions.TryGetValue(from, out var next))
        {
            next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _transitions[from] = next;
        }

        next[to] = next.TryGetValue(to, out var count) ? count + 1 : 1;
    }

    /// <summary>
    /// Mock operator history so the engine has patterns to work with on first launch
    /// (the brief allows simulated data). Real activity keeps adding to these counts.
    /// </summary>
    private void SeedHistoricalPatterns()
    {
        for (var i = 0; i < 4; i++) Bump("alert:Moisture Sensor B", "search:pump a");
        for (var i = 0; i < 3; i++) Bump("alert:Moisture Sensor B", "cmd:Pump A:On");
        for (var i = 0; i < 4; i++) Bump("alert:Smart Meter 1", "cmd:Relay R1:Off");
        for (var i = 0; i < 3; i++) Bump("alert:Moisture Sensor A", "cmd:Valve V1:On");
        for (var i = 0; i < 2; i++) Bump("alert:Smart Meter 2", "search:smart meter 1");
        for (var i = 0; i < 2; i++) Bump("cmd:Pump A:On", "search:moisture sensor b");
        _events = 0;
    }
}
