using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace ITSeti.Maintenance.Infrastructure;

public static class TreeSizeLauncher
{
    public static void StartScan(string volume)
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Для просмотра файлов без прав администратора откройте приложение из обычной учётной записи.");
        if (!Regex.IsMatch(volume, @"^[A-Za-z]:\\$"))
            throw new ArgumentException("Можно проверить только локальный диск.", nameof(volume));
        var drive = new DriveInfo(volume);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            throw new InvalidOperationException("Выбранный локальный диск недоступен.");

        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "TreeSize Free", "TreeSizeFree.exe");
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ITSeti", "Maintenance", "Tools", "TreeSize Free", "TreeSizeFree.exe");
        var exe = File.Exists(bundled) ? bundled : installed;
        if (!File.Exists(exe)) throw new FileNotFoundException("TreeSize Free не найден в комплекте приложения.");
        var info = CreateStartInfo(exe, volume);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось открыть TreeSize Free.");
    }

    internal static ProcessStartInfo CreateStartInfo(string exe, string volume)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        info.ArgumentList.Add(volume);
        return info;
    }
}
