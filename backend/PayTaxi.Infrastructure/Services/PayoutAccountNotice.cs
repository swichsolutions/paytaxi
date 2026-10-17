using PayTaxi.Core.Interfaces;

namespace PayTaxi.Infrastructure.Services;

/// <summary>
/// The words of the payout-account notice (<see cref="INotificationService.NotifyPayoutAccountChangedAsync"/>): the SMS in
/// the driver's language — Georgian, English or Russian; Georgian when the app never told us — and the English fallback of
/// the in-app notice (the app renders that one itself, from its type and data, in the language it is showing).
///
/// Who made the change is "you" or "your taxi park": the console's other users (the operator, Swich) act for the park as
/// far as the driver is concerned. Every SMS fits two segments (134 characters of Georgian or Cyrillic) with a
/// "TBC **** 1234" account label — the added-as-default ones say "payouts now go there" rather than spell out "default". The driver's own change ends "if this was not you"; the park's "if you did not ask
/// for this" — either way the next step is the park, which can see who did it (ParkConfigEvents, AddedBy).
/// </summary>
public static class PayoutAccountNotice
{
    public const string Type = "payout_account_changed";

    public static string Language(string? preferred) => preferred is "en" or "ru" ? preferred : "ka";

    /// <summary>The app's code for the change; added-as-default is "added" with <see cref="IsDefault"/> set (older apps read "added").</summary>
    public static string Code(PayoutAccountChange change) => change switch
    {
        PayoutAccountChange.Added or PayoutAccountChange.AddedAsDefault => "added",
        PayoutAccountChange.MadeDefault => "default",
        _ => "removed",
    };

    /// <summary>True when the account added is now where the payouts go.</summary>
    public static bool IsDefault(PayoutAccountChange change) => change == PayoutAccountChange.AddedAsDefault;

    public static string EnglishTitle(PayoutAccountChange change) => change switch
    {
        PayoutAccountChange.Added or PayoutAccountChange.AddedAsDefault => "Bank account added",
        PayoutAccountChange.MadeDefault => "Default account changed",
        _ => "Bank account removed",
    };

    /// <summary>The text without the "PayTaxi:" prefix the SMS carries.</summary>
    public static string Text(string? preferredLanguage, PayoutAccountChange change, bool byPark, string account) =>
        (Language(preferredLanguage), change, byPark) switch
        {
            ("ka", PayoutAccountChange.Added, false) => $"თქვენ დაამატეთ საბანკო ანგარიში {account} თანხის მისაღებად. თუ ეს თქვენ არ იყავით, დაუკავშირდით თქვენს ტაქსოპარკს.",
            ("ka", PayoutAccountChange.Added, true) => $"თქვენმა ტაქსოპარკმა თანხის მისაღებად დაგიმატათ საბანკო ანგარიში {account}. თუ ეს არ გითხოვიათ, დაუკავშირდით ტაქსოპარკს.",
            ("ka", PayoutAccountChange.AddedAsDefault, false) => $"თქვენ დაამატეთ ანგარიში {account}; თანხა ახლა მასზე ჩაირიცხება. თუ ეს თქვენ არ იყავით, დაუკავშირდით ტაქსოპარკს.",
            ("ka", PayoutAccountChange.AddedAsDefault, true) => $"თქვენმა ტაქსოპარკმა დაგიმატათ ანგარიში {account}; თანხა მასზე ჩაირიცხება. თუ ეს არ გითხოვიათ, დაუკავშირდით ტაქსოპარკს.",
            ("ka", PayoutAccountChange.MadeDefault, false) => $"თქვენ ძირითად ანგარიშად დააყენეთ {account}. თუ ეს თქვენ არ იყავით, დაუკავშირდით თქვენს ტაქსოპარკს.",
            ("ka", PayoutAccountChange.MadeDefault, true) => $"თქვენმა ტაქსოპარკმა თქვენს ძირითად ანგარიშად დააყენა {account}. თუ ეს თქვენ არ გითხოვიათ, დაუკავშირდით ტაქსოპარკს.",
            ("ka", _, false) => $"თქვენ წაშალეთ საბანკო ანგარიში {account}. თუ ეს თქვენ არ იყავით, დაუკავშირდით თქვენს ტაქსოპარკს.",
            ("ka", _, true) => $"თქვენმა ტაქსოპარკმა წაშალა თქვენი საბანკო ანგარიში {account}. თუ ეს თქვენ არ გითხოვიათ, დაუკავშირდით ტაქსოპარკს.",

            ("ru", PayoutAccountChange.Added, false) => $"Вы добавили банковский счёт {account} для выплат. Если это были не вы, обратитесь в свой таксопарк.",
            ("ru", PayoutAccountChange.Added, true) => $"Ваш таксопарк добавил банковский счёт {account} для ваших выплат. Если вы об этом не просили, обратитесь в таксопарк.",
            ("ru", PayoutAccountChange.AddedAsDefault, false) => $"Вы добавили счёт {account} для выплат и сделали его основным. Если это были не вы, обратитесь в свой таксопарк.",
            ("ru", PayoutAccountChange.AddedAsDefault, true) => $"Ваш таксопарк добавил счёт {account} и сделал его основным для выплат. Если вы этого не просили, обратитесь в таксопарк.",
            ("ru", PayoutAccountChange.MadeDefault, false) => $"Вы сделали счёт {account} основным для выплат. Если это были не вы, обратитесь в свой таксопарк.",
            ("ru", PayoutAccountChange.MadeDefault, true) => $"Ваш таксопарк сделал счёт {account} основным для ваших выплат. Если вы об этом не просили, обратитесь в таксопарк.",
            ("ru", _, false) => $"Вы удалили банковский счёт {account}. Если это были не вы, обратитесь в свой таксопарк.",
            ("ru", _, true) => $"Ваш таксопарк удалил ваш банковский счёт {account}. Если вы об этом не просили, обратитесь в таксопарк.",

            (_, PayoutAccountChange.Added, false) => $"You added the bank account {account} for payouts. If this was not you, contact your taxi park.",
            (_, PayoutAccountChange.Added, true) => $"Your taxi park added the bank account {account} for your payouts. If you did not ask for this, contact your taxi park.",
            (_, PayoutAccountChange.AddedAsDefault, false) => $"You added the bank account {account} and made it your default account for payouts. If this was not you, contact your taxi park.",
            (_, PayoutAccountChange.AddedAsDefault, true) => $"Your taxi park added the bank account {account} and made it your default account for payouts. If you did not ask for this, contact your taxi park.",
            (_, PayoutAccountChange.MadeDefault, false) => $"You made {account} your default account for payouts. If this was not you, contact your taxi park.",
            (_, PayoutAccountChange.MadeDefault, true) => $"Your taxi park made {account} your default account for payouts. If you did not ask for this, contact your taxi park.",
            (_, _, false) => $"You removed the bank account {account}. If this was not you, contact your taxi park.",
            (_, _, true) => $"Your taxi park removed your bank account {account}. If you did not ask for this, contact your taxi park.",
        };

    public static string Sms(string? preferredLanguage, PayoutAccountChange change, bool byPark, string account) =>
        "PayTaxi: " + Text(preferredLanguage, change, byPark, account);
}
