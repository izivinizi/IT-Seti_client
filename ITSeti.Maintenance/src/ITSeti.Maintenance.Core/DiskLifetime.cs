namespace ITSeti.Maintenance.Core;

public static class DiskLifetime
{
    public const long WarningThresholdHours = 60_000;

    public static bool ExceedsWarning(long? hours) => hours is > WarningThresholdHours;

    public static string Format(long? hours)
    {
        if (hours is not long value || value < 0) return "";
        var daysTotal = value / 24;
        return $"{value:N0} ч (около {daysTotal / 365} г. {daysTotal % 365} дн.)";
    }
}
