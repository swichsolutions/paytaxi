namespace PayTaxi.Core.Entities;

/// <summary>
/// An in-app notification delivered to a driver. The real-channel side
/// (SMS / push) will be a separate sender that reads from the same row.
/// </summary>
public class Notification : BaseEntity
{
    public Guid DriverId { get; set; }

    /// <summary>Stable kind: <c>cashout_completed</c>, <c>cashout_failed</c>, <c>cashout_review</c>, etc.</summary>
    public string Type { get; set; } = default!;

    public string Title { get; set; } = default!;
    public string Body  { get; set; } = default!;

    /// <summary>Optional in-app deep link (e.g. <c>/history</c>).</summary>
    public string? Link { get; set; }

    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }

    public Driver Driver { get; set; } = default!;
}
