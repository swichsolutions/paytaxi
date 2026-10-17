using Microsoft.EntityFrameworkCore;
using PayTaxi.Core.Banking;
using PayTaxi.Core.Entities;
using PayTaxi.Core.Identity;
using PayTaxi.Core.Interfaces;
using PayTaxi.Infrastructure.Data;
using PayTaxi.Infrastructure.Security;

namespace PayTaxi.Api.Controllers;

/// <summary>
/// Shared logic for adding / removing a driver's payout destination (IBAN), used by
/// both the driver app endpoints and the operator-side admin endpoints so the rules
/// are identical: valid Georgian IBAN, the park must hold a payout account at that
/// bank (TBC-only at launch), no duplicates per driver.
///
/// Ownership rule: the account holder name must look like the driver's registered name
/// (<see cref="PersonName.LooksLikeSamePerson"/>, script-blind). A driver typing someone
/// else's name is refused with <c>holder_name_mismatch</c> and told to contact the park.
/// A park admin MAY add a third-party account, but must give a reason; the destination is
/// then flagged <see cref="BankCard.IsThirdPartyAccount"/> for every later screen and invoice.
/// The bank routes by IBAN and ignores the name, so this is documentation, not prevention —
/// which is why the admin path exists and is recorded.
///
/// Every add, change of default and removal — the driver's own or the park side's — is told to the driver
/// (<see cref="NotifyAsync"/>: an in-app notice and an SMS), after it is saved and without ever failing it: where the
/// money goes is the driver's to know, and a change they did not make is how a stolen session or a rogue manager shows.
/// </summary>
public static class DestinationHelper
{
    public enum Outcome { Created, BadRequest, Conflict }

    public static async Task<(Outcome, string? error, object? payload, BankCard? card)> AddAsync(
        AppDbContext db, Guid parkId, Driver driver, string? rawIban, string? holderName, bool makeDefault,
        bool allowThirdParty, string? thirdPartyReason, string initiatedBy, CancellationToken ct)
    {
        if (!GeorgianIban.TryParse(rawIban, out var iban, out var bankCode, out var ibanError))
            return (Outcome.BadRequest, ibanError, new { error = ibanError }, null);

        // ── Input bounds (the columns are varchar(200)/varchar(500); a DB error would be a 500) ──
        holderName = string.IsNullOrWhiteSpace(holderName) ? null : holderName.Trim();
        thirdPartyReason = string.IsNullOrWhiteSpace(thirdPartyReason) ? null : thirdPartyReason.Trim();
        if (holderName is { Length: > 200 })
            return (Outcome.BadRequest, "holder_name_too_long", new { error = "holder_name_too_long", max = 200 }, null);
        if (thirdPartyReason is { Length: > 500 })
            return (Outcome.BadRequest, "reason_too_long", new { error = "reason_too_long", max = 500 }, null);

        // ── Ownership: does the typed holder look like this driver? ──────
        // A driver imported without a name may carry a placeholder — that is no name at all.
        var registeredName = IsPlaceholderName(driver.Name) ? null : driver.Name;
        var typedHolder = holderName ?? registeredName ?? "";
        // If our own record has no usable name we cannot judge — that is the park's data gap,
        // not the driver's fault, so don't block him on it.
        var canJudge = PersonName.Words(registeredName).Count > 0 && PersonName.Words(typedHolder).Count > 0;
        var isThirdParty = canJudge && !PersonName.LooksLikeSamePerson(registeredName, typedHolder);

        if (isThirdParty && !allowThirdParty)
        {
            return (Outcome.BadRequest, "holder_name_mismatch", new
            {
                error = "holder_name_mismatch",
                registeredName = driver.Name,
                typedName = typedHolder,
            }, null);
        }
        if (isThirdParty && string.IsNullOrWhiteSpace(thirdPartyReason))
        {
            return (Outcome.BadRequest, "third_party_reason_required", new
            {
                error = "third_party_reason_required",
                registeredName = driver.Name,
                typedName = typedHolder,
            }, null);
        }

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
            }, null);
        }

        var ibanHash = LookupHash.Iban(iban);
        var existing = await db.BankCards
            .FirstOrDefaultAsync(b => b.DriverId == driver.Id && b.IbanHash == ibanHash, ct);
        if (existing is not null && existing.IsActive)
            return (Outcome.Conflict, "iban_already_added", null, null);

        // An IBAN the park once registered as a third party's stays a third party's. A driver
        // may not remove it and re-add it under his own name to shed the flag; only the park can.
        if (existing is { IsThirdPartyAccount: true } && !allowThirdParty)
        {
            return (Outcome.BadRequest, "third_party_account_locked", new
            {
                error = "third_party_account_locked",
                holderName = existing.HolderName,
            }, null);
        }

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
            existing.HolderName = typedHolder;
            existing.IsThirdPartyAccount = isThirdParty;
            existing.ThirdPartyReason = isThirdParty ? thirdPartyReason : null;
            existing.AddedBy = initiatedBy;
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
                HolderName = typedHolder,
                IsThirdPartyAccount = isThirdParty,
                ThirdPartyReason = isThirdParty ? thirdPartyReason : null,
                AddedBy = initiatedBy,
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
            isThirdPartyAccount = card.IsThirdPartyAccount,
            thirdPartyReason = card.ThirdPartyReason,
            addedBy = card.AddedBy,
        }, card);
    }

    /// <summary>Values that mean "no name was recorded", not a name: legacy import placeholder, dashes, empty.</summary>
    public static bool IsPlaceholderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var t = name.Trim();
        return t is "(unnamed)" or "unnamed" or "-" or "—" or "n/a" or "N/A";
    }

    /// <summary>The removed destination, or null when the driver has no such active one.</summary>
    public static async Task<BankCard?> DeactivateAsync(AppDbContext db, Guid driverId, Guid cardId, CancellationToken ct)
    {
        var card = await db.BankCards.FirstOrDefaultAsync(b => b.Id == cardId && b.DriverId == driverId && b.IsActive, ct);
        if (card is null) return null;

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
        return card;
    }

    /// <summary>The destination made default (null when the driver has no such active one) and whether it was not already.</summary>
    public static async Task<(BankCard? card, bool changed)> SetDefaultAsync(AppDbContext db, Guid driverId, Guid cardId, CancellationToken ct)
    {
        var card = await db.BankCards.FirstOrDefaultAsync(b => b.Id == cardId && b.DriverId == driverId && b.IsActive, ct);
        if (card is null) return (null, false);
        var changed = !card.IsDefault;
        await db.BankCards
            .Where(b => b.DriverId == driverId && b.IsDefault && b.Id != cardId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.IsDefault, false), ct);
        card.IsDefault = true;
        await db.SaveChangesAsync(ct);
        return (card, changed);
    }

    /// <summary>
    /// Tells the driver about a saved change of <paramref name="card"/> (in-app notice + SMS, masked as the app shows it).
    /// <paramref name="byPark"/>: made from the console — the park's manager, the operator or Swich ("your taxi park").
    /// An account added as the default is told as such: the payouts go there from now on (a console add is the default
    /// unless it says otherwise), and "added" alone hid that. Not cancellable by the request: the change is already saved,
    /// and an aborted request must not swallow its notice. Never throws (the notifier logs its own failures).
    /// </summary>
    public static Task NotifyAsync(IServiceProvider services, BankCard card, PayoutAccountChange change, bool byPark) =>
        services.GetRequiredService<INotificationService>().NotifyPayoutAccountChangedAsync(
            card.DriverId, change == PayoutAccountChange.Added && card.IsDefault ? PayoutAccountChange.AddedAsDefault : change,
            $"{card.BankType} {card.MaskedPan}", byPark, CancellationToken.None);
}
