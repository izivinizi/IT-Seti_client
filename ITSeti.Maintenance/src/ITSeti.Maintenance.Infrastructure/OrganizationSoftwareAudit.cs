using System.Diagnostics;
using Microsoft.Win32;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record OrganizationComponent(string Name, string State, string Detail, string Package, bool NeedsAttention, string InstallKey)
{
    public bool CanInstall => !string.IsNullOrWhiteSpace(InstallKey) &&
        (State == "Установлено" || Package == "Файл есть");
    public string InstallLabel => State == "Установлено" ? "Удалить" : "Установить";
}

public static class OrganizationSoftwareAudit
{
    public static string? FindSource()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Setup", "ITSETI-Setup");
        if (File.Exists(Path.Combine(bundled, "system", "Install.ps1"))) return bundled;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Removable or DriveType.Fixed))
        {
            try
            {
                if (!drive.IsReady) continue;
                foreach (var source in new[]
                {
                    Path.Combine(drive.RootDirectory.FullName, "ITSETI-Setup"),
                    Path.Combine(drive.RootDirectory.FullName, "Service", "ITSETI-Setup")
                })
                    if (File.Exists(Path.Combine(source, "system", "Install.ps1"))) return source;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IReadOnlyList<OrganizationComponent> Inspect(string? source)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFiles86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var anyDesk = Existing(programFiles, programFiles86, "AnyDesk", "AnyDesk.exe");
        var rms = Existing(programFiles, programFiles86, "Remote Manipulator System - Host", "rutserv.exe");
        var ocs = Path.Combine(programFiles, "OCS Inventory Agent", "OcsService.exe");
        var desktop = Path.Combine(programFiles, "Desktop Info", "DesktopInfo.exe");
        var panel = Path.Combine(data, "ITSETI", "DesktopInfo.ini");
        var rows = new List<OrganizationComponent>
        {
            Evaluate("AnyDesk", "AnyDesk", anyDesk, HasService("AnyDesk"), source, "AnyDesk-installer.exe"),
            Evaluate("RMS Host", "RMS", rms, HasService("RManService"), source, "Host-IT-SETI.RMS.7.7.3.0v3.msi", new Version(7, 7, 3, 0)),
            Evaluate("OCS Inventory", "OCS", File.Exists(ocs) ? ocs : null, HasService("OCS Inventory Service"), source, "OCS-Agent-Installerv4.exe"),
            Evaluate("Панель ИТ-Сети", "Panel", File.Exists(desktop) ? desktop : null, File.Exists(panel), source, "DesktopInfo3230.exe", new Version(3, 23, 0))
        };
        return rows;
    }

    private static OrganizationComponent Evaluate(string name, string installKey, string? exe, bool serviceOrPanel, string? source,
        string package, Version? required = null)
    {
        var file = exe is not null && File.Exists(exe);
        var version = file ? FileVersionInfo.GetVersionInfo(exe!).FileVersion : null;
        var current = Version.TryParse(version?.Split(' ').FirstOrDefault(), out var parsed) ? parsed : null;
        var old = required is not null && current is not null && current < required;
        var unknownVersion = required is not null && file && current is null;
        var state = file && serviceOrPanel && !old && !unknownVersion ? "Установлено" : file || serviceOrPanel ? "Требует проверки" : "Не найдено";
        var detail = unknownVersion ? "Версию файла определить не удалось; нужна ручная проверка." : old ? $"Версия {version}; нужна не ниже {required}." : file && serviceOrPanel
            ? $"Найдены файлы и {(name == "Панель ИТ-Сети" ? "панель" : "служба")}." : file
            ? "Файлы есть, но служба или панель не найдена." : serviceOrPanel
            ? "Служба или панель есть, но файл программы не найден." : "Программа не обнаружена по штатному пути.";
        return new(name, state, detail, PackageState(source, "packages", package), state != "Установлено", installKey);
    }

    private static string PackageState(string? source, string folder, string file)
    {
        if (source is null) return "Комплект не подключён";
        var path = folder == ".." ? Path.Combine(source, "system", file)
            : Path.Combine(source, "system", folder, file);
        return File.Exists(path) ? "Файл есть" : "Нет в комплекте";
    }

    private static string? Existing(string firstRoot, string secondRoot, string folder, string file)
    {
        foreach (var root in new[] { firstRoot, secondRoot })
        {
            var candidate = Path.Combine(root, folder, file);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool HasService(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key is not null;
    }
}
