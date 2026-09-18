namespace PayTaxi.Infrastructure.Adapters.Yandex;

public class YandexFleetOptions
{
    public const string SectionName = "YandexFleet";

    /// <summary>Use the in-memory mock client instead of the real HTTP client.</summary>
    public bool UseMock { get; set; } = true;

    /// <summary>
    /// When true, the write endpoints (PostCashoutTransactionAsync / PostReversalTransactionAsync)
    /// throw YandexReadOnlyModeException. Defaults to true so accidents don't move money.
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

    // ── Real HTTP client ─────────────────────────────────────────────

    /// <summary>Fleet API host. Same for all parks; credentials are per park.</summary>
    public string BaseUrl { get; set; } = "https://fleet-api.taxi.yandex.net/";

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Sent as Accept-Language; Yandex localises category names and messages.</summary>
    public string AcceptLanguage { get; set; } = "en";

    /// <summary>
    /// Transaction category for the cashout debit. CLAUDE.md says "partner_service_manual";
    /// PAYTAXI-CONTEXT.md §9 asks to confirm against the category paypro uses in the park's
    /// Fleet history. Overridable per environment without a code change.
    /// </summary>
    public string CashoutCategoryId { get; set; } = "partner_service_manual";

    /// <summary>Category for the compensating +amount when a payout is abandoned. Defaults to the cashout category.</summary>
    public string? ReversalCategoryId { get; set; }

    /// <summary>Page size for list endpoints (Yandex maximum is 1000 for profiles, 500 for orders).</summary>
    public int PageSize { get; set; } = 500;
}
