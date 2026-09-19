using System.Security.Cryptography;
using System.Text;

namespace PayTaxi.Core.Identity;

/// <summary>
/// The one phone normaliser. Login, admin onboarding, CSV import and the seed must all
/// produce the same canonical string, because <c>Drivers.PhoneHash</c> is SHA-256 of it and
/// the encrypted phone column can't be searched. Canonical form: <c>+995XXXXXXXXX</c>.
///
/// Accepted inputs (all → +995599123456): "599123456", "599 12 34 56", "+995 599 123 456",
/// "995599123456", "00995599123456", "0599123456". Foreign numbers keep their own country
/// code when written with a leading + (or 00) and at least 9 digits.
/// </summary>
public static class GeorgianPhone
{
    public const string CountryCode = "995";
    public const int LocalDigits = 9;

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        var hasPlus = trimmed.StartsWith('+');
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (digits.StartsWith("00") && digits.Length > LocalDigits + 2)
        {
            digits = digits[2..];
            hasPlus = true;
        }

        if (digits.Length < LocalDigits) return null;

        // Local formats: 9 digits, or a trunk 0 + 9 digits.
        if (!hasPlus && digits.Length == LocalDigits)
            return "+" + CountryCode + digits;
        if (!hasPlus && digits.Length == LocalDigits + 1 && digits.StartsWith('0'))
            return "+" + CountryCode + digits[1..];

        // Country code written without +.
        if (digits.StartsWith(CountryCode) && digits.Length == CountryCode.Length + LocalDigits)
            return "+" + digits;

        // Anything else with a + and enough digits: keep as international.
        if (hasPlus && digits.Length >= LocalDigits && digits.Length <= 15)
            return "+" + digits;

        return null;
    }

    /// <summary>True when the canonical form is a Georgian mobile/landline (+995 + 9 digits).</summary>
    public static bool IsGeorgian(string canonical) =>
        canonical.StartsWith("+" + CountryCode) && canonical.Length == 1 + CountryCode.Length + LocalDigits;

    /// <summary>SHA-256 hex (lowercase) of the canonical phone — the lookup column.</summary>
    public static string Hash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>"+995 599 *** 456" — for logs and UI.</summary>
    public static string Mask(string canonical)
    {
        if (string.IsNullOrEmpty(canonical) || canonical.Length < 6) return "***";
        return canonical[..^6] + "***" + canonical[^3..];
    }
}
