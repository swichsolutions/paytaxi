using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace PayTaxi.Core.Banking;

/// <summary>
/// Georgian IBAN helpers. Format: GE + 2 check digits + 2-letter bank code + 16 digits (22 chars).
/// The bank code is what routes a payout to the park account at the same bank
/// (intra-bank transfers are free and instant on TBC).
/// </summary>
public static class GeorgianIban
{
    private static readonly Regex Shape = new("^GE[0-9]{2}[A-Z]{2}[0-9]{16}$", RegexOptions.Compiled);

    /// <summary>Known Georgian bank codes → display label. Unknown codes are still accepted; the label falls back to the code.</summary>
    public static readonly IReadOnlyDictionary<string, string> BankLabels = new Dictionary<string, string>
    {
        ["TB"] = "TBC",
        ["BG"] = "BOG",
        ["LB"] = "LIBERTY",
        ["BS"] = "BASIS",
        ["CD"] = "CARTU",
        ["PC"] = "PROCREDIT",
        ["TR"] = "TERA",
        ["CR"] = "CREDO",
        ["HB"] = "HALYK",
        ["IS"] = "ISBANK",
        ["ZR"] = "ZIRAAT",
        ["PH"] = "PASHA",
        ["SL"] = "SILKROAD",
        ["NB"] = "NBG",
    };

    /// <summary>Uppercase, strip spaces/hyphens.</summary>
    public static string Normalize(string raw) =>
        new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

    /// <summary>
    /// Validate shape and ISO 7064 mod-97 check digits. Returns the normalized IBAN
    /// and its bank code, or an error code suitable for an API response.
    /// </summary>
    public static bool TryParse(string? raw, out string iban, out string bankCode, out string error)
    {
        iban = ""; bankCode = ""; error = "";
        if (string.IsNullOrWhiteSpace(raw)) { error = "iban_required"; return false; }

        var n = Normalize(raw);
        if (!Shape.IsMatch(n)) { error = "invalid_iban_format"; return false; }
        if (!Mod97Valid(n)) { error = "invalid_iban_checksum"; return false; }

        iban = n;
        bankCode = n.Substring(4, 2);
        return true;
    }

    public static string BankLabel(string bankCode) =>
        BankLabels.TryGetValue(bankCode, out var l) ? l : bankCode;

    /// <summary>"**** 4521" style display string.</summary>
    public static string Mask(string iban) =>
        iban.Length >= 4 ? $"**** {iban[^4..]}" : "****";

    /// <summary>Build a valid Georgian IBAN (correct check digits) — used by seed/mock data only.</summary>
    public static string Build(string bankCode, string sixteenDigits)
    {
        if (bankCode.Length != 2 || sixteenDigits.Length != 16)
            throw new ArgumentException("bankCode must be 2 letters, account must be 16 digits");
        var body = $"{bankCode.ToUpperInvariant()}{sixteenDigits}GE00";
        var check = 98 - (int)(ToNumeric(body) % 97);
        return $"GE{check:00}{bankCode.ToUpperInvariant()}{sixteenDigits}";
    }

    private static bool Mod97Valid(string iban)
    {
        // Move the first 4 chars to the end, map letters A=10..Z=35, mod 97 must be 1.
        var rearranged = iban[4..] + iban[..4];
        return ToNumeric(rearranged) % 97 == 1;
    }

    private static BigInteger ToNumeric(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var c in s)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else if (char.IsLetter(c)) sb.Append(c - 'A' + 10);
            else throw new ArgumentException($"Invalid IBAN char '{c}'");
        }
        return BigInteger.Parse(sb.ToString());
    }
}
