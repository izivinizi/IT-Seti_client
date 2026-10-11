namespace ITSeti.Maintenance.Core;

public static class HwInfoThresholds
{
    public const double CpuWarningC = 75;
    public const double CpuCriticalC = 85;
    public const double VrmWarningC = 80;
    public const double VrmCriticalC = 95;
    public const double PchWarningC = 70;
    public const double PchCriticalC = 80;

    public const double Rail12WarningMinV = 11.4;
    public const double Rail12WarningMaxV = 12.6;
    public const double Rail12CriticalMinV = 10.8;
    public const double Rail12CriticalMaxV = 13.2;
    public const double Rail5WarningMinV = 4.75;
    public const double Rail5WarningMaxV = 5.25;
    public const double Rail5CriticalMinV = 4.5;
    public const double Rail5CriticalMaxV = 5.5;
}
