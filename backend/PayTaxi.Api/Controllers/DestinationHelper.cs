using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Entities;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Shared logic for adding / removing a driver's payout destination (IBAN), used by
/// both the driver app endpoints and the operator-side admin endpoints so the rules
/// are identical: valid Georgian IBAN, the park must hold a payout account at that
/// bank (TBC-only at launch), no duplicates per driver.
/// </summary>
public static class DestinationHelper
{
    public enum Outcome { Created, BadRequest, Conflict }

    public static async Task<(Outcome, string? error, object? payload)> AddAsync(
        AppDbContext db, Guid parkId, Driver driver, string? rawIban, string? holderName, bool makeDefault, CancellationToken ct)
    {
        if (!GeorgianIban.TryParse(rawIban, out var iban, out var bankCode, out var ibanError))
            return (Outcome.BadRequest, ibanError, new { error = ibanError });

        var supportedCodes = await db.ParkBankAccounts.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.IsActive)
            .Select(a => a.BankCode)
            .Distinct()
            .ToListAsync(ct);

        if (!supportedCodes.Contains(bankCode))
        {
            return (Outcome.BadRequest, "bank_not_supported", new
            {
                error = "bank_not_supported",
                bankCode,
                bankLabel = GeorgianIban.BankLabel(bankCode),
                supported = supportedCodes.Select(c => new { bankCode = c, bankLabel = GeorgianIban.BankLabel(c) }),
            });
        }

        var ibanHash = FieldEncryptor.Hash(iban);
        var existing = await db.BankCards
            .FirstOrDefaultAsync(b => b.DriverId == driver.Id && b.IbanHash == ibanHash, ct);
        if (existing is not null && existing.IsActive)
            return (Outcome.Conflict, "iban_already_added", null);

        var hasActive = await db.BankCards.AnyAsync(b => b.DriverId == driver.Id && b.IsActive, ct);
        var isDefault = makeDefault || !hasActive;
        if (isDefault)
        {
            await db.BankCards
                .Where(b => b.DriverId == driver.Id && b.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false), ct);
        }

        BankCard card;
        if (existing is not null)
        {
            // Re-activate a previously removed destination instead of duplicating it.
            existing.IsActive = true;
            existing.IsDefault = isDefault;
            existing.HolderName = string.IsNullOrWhiteSpace(holderName) ? existing.HolderName : holderName.Trim();
            card = existing;
        }
        else
        {
            card = new BankCard
            {
                DriverId = driver.Id,
                Iban = iban,
                IbanHash = ibanHash,
                BankCode = bankCode,
                BankType = GeorgianIban.BankLabel(bankCode),
                MaskedPan = GeorgianIban.Mask(iban),
                HolderName = string.IsNullOrWhiteSpace(holderName) ? driver.Name : holderName.Trim(),
                TokenReferenceEncrypted = "",
                IsDefault = isDefault,
                IsActive = true,
            };
            db.BankCards.Add(card);
        }

        await db.SaveChangesAsync(ct);

        return (Outcome.Created, null, new
        {
            id = card.Id,
            iban = card.Iban,
            maskedPan = card.MaskedPan,
            bankType = card.BankType,
            bankCode = card.BankCode,
            holderName = card.HolderName,
            isDefault = card.IsDefault,
        });
    }

    public static async Task<bool> DeactivateAsync(AppDbContext db, Guid driverId, Guid cardId, CancellationToken ct)
    {
        var card = await db.BankCards.FirstOrDefaultAsync(b => b.Id == cardId && b.DriverId == driverId && b.IsActive, ct);
        if (card is null) return false;

        card.IsActive = false;
        var wasDefault = card.IsDefault;
        card.IsDefault = false;
        await db.SaveChangesAsync(ct);

        if (wasDefault)
        {
            // Promote the most recent remaining destination so the app always has a default.
            var next = await db.BankCards
                .Where(b => b.DriverId == driverId && b.IsActive)
                .OrderByDescending(b => b.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                next.IsDefault = true;
                await db.SaveChangesAsync(ct);
            }
        }
        return true;
    }

    public static async Task<bool> SetDefaultAsync(AppDbContext db, Guid driverId, Guid cardId, CancellationToken ct)
    {
        var card = await db.BankCards.FirstOrDefaultAsync(b => b.Id == cardId && b.DriverId == driverId && b.IsActive, ct);
        if (card is null) return false;
        await db.BankCards
            .Where(b => b.DriverId == driverId && b.IsDefault && b.Id != cardId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false), ct);
        card.IsDefault = true;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
