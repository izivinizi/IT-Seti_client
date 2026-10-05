using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Infrastructure;

public static class WindowsVersionInfo
{
    public static (string? Edition, string? Release, int? Build) Read()
    {
        string? product = null, release = null;
        int? build = null;
        var workstation = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = key?.GetValue("ProductName")?.ToString();
            release = (key?.GetValue("DisplayVersion") ?? key?.GetValue("ReleaseId"))?.ToString();
            if (int.TryParse((key?.GetValue("CurrentBuildNumber") ?? key?.GetValue("CurrentBuild"))?.ToString(), out var number))
                build = number;
        }
        catch (System.Security.SecurityException) { }
        catch (IOException) { }
        var version = new OsVersion { Size = (uint)Marshal.SizeOf<OsVersion>(), ServicePack = "" };
        if (RtlGetVersion(ref version) == 0)
        {
            build = (int)version.Build;
            workstation = version.ProductType == 1;
        }
        return (NormalizeEdition(product, build, workstation), release, build);
    }

    public static string? NormalizeEdition(string? name, int? build, bool workstation)
    {
        if (workstation && build >= 22000)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Windows 11";
            return name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }
        return name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersion
    {
        public uint Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack;
        public ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        public byte ProductType, Reserved;
    }

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern int RtlGetVersion(ref OsVersion version);
}
