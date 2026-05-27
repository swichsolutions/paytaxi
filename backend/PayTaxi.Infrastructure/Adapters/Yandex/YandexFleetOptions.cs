namespace PayTaxi.Infrastructure.Adapters.Yandex;

public class YandexFleetOptions
{
    public const string SectionName = "YandexFleet";

    /// <summary>Use the in-memory mock client instead of the real HTTP client.</summary>
    public bool UseMock { get; set; } = true;

    /// <summary>
    /// When true, the write endpoint (PostCashoutTransactionAsync) throws YandexReadOnlyModeException.
    /// Defaults to true so accidents don't move money.
    /// </summary>
    public bool ReadOnlyMode { get; set; } = true;

    /// <summary>Minimum milliseconds between calls targeting the same park (Yandex empirical limit).</summary>
    public int MinIntervalMs { get; set; } = 500;

    /// <summary>Maximum retry attempts on transient failures (excluding the initial try).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay for exponential backoff (1st retry waits this; 2nd waits 2x; 3rd waits 4x).</summary>
    public int RetryBaseDelayMs { get; set; } = 250;

    /// <summary>Simulated latency for mock calls (ms).</summary>
    public int MockLatencyMs { get; set; } = 120;

    /// <summary>Probability (0..1) of the mock returning a transient failure to exercise retry.</summary>
    public double MockTransientFailureRate { get; set; } = 0.03;
}
