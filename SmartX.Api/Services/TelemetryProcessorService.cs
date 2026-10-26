namespace SmartX.Api.Services;

/// <summary>
/// Background worker that drains the telemetry queues. Packets are processed in small batches
/// every 100 ms, so a burst of packets visibly waits in the FIFO queue while a critical alert
/// (priority queue) is served first.
/// </summary>
public class TelemetryProcessorService : BackgroundService
{
    private const int BatchSize = 10;

    private readonly StreamEngine _engine;
    private readonly TimeSpan _disconnectAfter;

    public TelemetryProcessorService(StreamEngine engine, IConfiguration configuration)
    {
        _engine = engine;
        var seconds = configuration.GetValue<int?>("Stream:DisconnectAfterSeconds") ?? 20;
        _disconnectAfter = TimeSpan.FromSeconds(Math.Max(5, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastWatchdog = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _engine.ProcessPending(BatchSize);

                if ((DateTime.UtcNow - lastWatchdog).TotalSeconds >= 1)
                {
                    _engine.RunWatchdog(_disconnectAfter);
                    lastWatchdog = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // A bad packet must never kill the pipeline.
                Console.WriteLine($"[processor] {ex.Message}");
            }

            try
            {
                await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
