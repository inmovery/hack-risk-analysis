using System.Globalization;

namespace Aeza.Risk.Core;

/// <summary>Форматирование для текстов причин (культурно-независимо: контейнер работает с InvariantGlobalization)</summary>
public static class Fmt
{
    public static string Eur(long minor)
    {
        var sign = minor < 0 ? "−" : "";
        var abs = Math.Abs((decimal)minor);
        var euros = decimal.Truncate(abs / 100m);
        var cents = (int)(abs - euros * 100m);
        var whole = euros.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", " ");
        return $"{sign}€{whole},{cents:00}";
    }

    public static string Date(DateTimeOffset d) => d.UtcDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC";

    public static string Num(decimal v, int decimals = 1) =>
        decimal.Round(v, decimals, MidpointRounding.AwayFromZero).ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>«через 3 ч», «через 2,5 дн.», «сейчас», «4 дн. назад»</summary>
    public static string Relative(DateTimeOffset due, DateTimeOffset asOf)
    {
        var delta = due - asOf;
        if (Math.Abs(delta.TotalMinutes) < 1) return "сейчас";
        var future = delta > TimeSpan.Zero;
        var abs = delta.Duration();
        var text = abs.TotalHours < 48 ? $"{Num((decimal)abs.TotalHours)} ч" : $"{Num((decimal)abs.TotalDays)} дн.";
        return future ? $"через {text}" : $"{text} назад";
    }

    public static string Term(PaymentTerm t) => t switch
    {
        PaymentTerm.Hour => "час",
        PaymentTerm.Week => "неделю",
        PaymentTerm.Month => "месяц",
        PaymentTerm.QuarterYear => "квартал",
        PaymentTerm.HalfYear => "полгода",
        PaymentTerm.Year => "год",
        _ => "период",
    };
}
