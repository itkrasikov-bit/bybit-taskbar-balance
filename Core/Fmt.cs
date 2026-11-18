using System.Globalization;

namespace BybitTaskbar.Core;

internal static class Fmt
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Полная сумма: $1 234,56 (для попапа).</summary>
    public static string Money(decimal v)
    {
        var abs = Math.Abs(v);
        var s = abs switch
        {
            >= 1_000_000m => v.ToString("#,##0", Ru),
            >= 0.01m => v.ToString("#,##0.00", Ru),
            > 0m => v.ToString("0.####", Ru),
            _ => "0"
        };
        return (v < 0 ? "-$" : "$") + s;
    }

    /// <summary>Компактная сумма для чипа на панели задач: $12 345,67 / $123,4k / $1,23M.</summary>
    public static string MoneyCompact(decimal v)
    {
        var sign = v < 0 ? "-$" : "$";
        var a = Math.Abs(v);
        if (a >= 1_000_000m)
            return sign + (a / 1_000_000m).ToString("0.##", Ru) + "M";
        if (a >= 100_000m)
            return sign + (a / 1_000m).ToString("0.#", Ru) + "k";
        if (a >= 0.01m || a == 0m)
            return sign + a.ToString("#,##0.00", Ru);
        return sign + a.ToString("0.####", Ru);
    }

    public static string Percent(decimal v) => v.ToString("0.##", Ru) + "%";
}
