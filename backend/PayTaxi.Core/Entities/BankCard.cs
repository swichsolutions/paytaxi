namespace PayTaxi.Core.Entities;

public class BankCard : BaseEntity
{
    public Guid DriverId { get; set; }
    public string MaskedPan { get; set; } = default!; // e.g. **** 1234
    public string TokenReferenceEncrypted { get; set; } = default!;
    public string BankType { get; set; } = default!; // "BOG" | "TBC"
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;

    public Driver Driver { get; set; } = default!;
}
