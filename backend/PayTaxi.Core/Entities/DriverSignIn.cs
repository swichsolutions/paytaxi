namespace PayTaxi.Core.Entities;

/// <summary>
/// One sign-in of a driver with an SMS code — the sign-in history. Signing in is also where the driver accepts the
/// Terms of Use, so this row is the record of that acceptance. It outlives the device session it opened: the session
/// (<see cref="DriverSession"/>) is deleted 90 days after its last use, this row after 2 years (RetentionWorker; the
/// privacy notice promises both periods). A token refresh is not a sign-in and writes nothing here.
///
/// Only what the notice lists: when, which driver and park, and the short device hint ("Android · Chrome") — never
/// the IP address or the raw User-Agent.
/// </summary>
public class DriverSignIn
{
    public long Id { get; set; }
    public Guid DriverId { get; set; }
    public Guid ParkId { get; set; }
    public DateTime SignedInAt { get; set; } = DateTime.UtcNow;
    public string? DeviceLabel { get; set; }

    public Driver Driver { get; set; } = default!;
}
