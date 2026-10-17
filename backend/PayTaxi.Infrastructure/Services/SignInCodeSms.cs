namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The words of the sign-in SMS, in the driver's language (the same rule as <see cref="PayoutAccountNotice.Language"/>:
/// Georgian when the app never told us). One segment each — 70 characters is the limit for Georgian and Cyrillic, 160
/// for Latin — so every code costs one message and arrives in one piece.
/// </summary>
public static class SignInCodeSms
{
    /// <summary>The purpose tag the sender logs and puts in the gateway's reference ("signin-…").</summary>
    public const string Purpose = "signin";

    public static string Text(string? preferredLanguage, string code, int validMinutes) =>
        PayoutAccountNotice.Language(preferredLanguage) switch
        {
            "en" => $"PayTaxi code: {code}. Valid {validMinutes} minutes.",
            "ru" => $"PayTaxi: ваш код {code}. Действует {validMinutes} минут.",
            _ => $"PayTaxi: თქვენი კოდია {code}. მოქმედებს {validMinutes} წუთი.",
        };
}
