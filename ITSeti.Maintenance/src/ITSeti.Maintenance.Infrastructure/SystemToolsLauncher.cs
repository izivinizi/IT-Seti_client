using System.Diagnostics;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record SystemToolShortcut(string Key, string Title, string Icon, bool Available, string ToolTip);

public static class SystemToolsLauncher
{
    private sealed record Tool(string Key, string Title, string Icon, string FileName, string[] Arguments, bool Shell = false);

    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string[] ToolKeys = ["computer", "policy", "terminal", "control", "settings", "adapters", "info", "security"];

    public static IReadOnlyList<SystemToolShortcut> GetShortcuts() => ToolKeys.Select(Create).ToArray();

    public static Process? Launch(string key)
    {
        var tool = Resolve(key);
        if (!File.Exists(tool.FileName) && !tool.Shell)
            throw new FileNotFoundException("Системное приложение недоступно в этой редакции Windows.", tool.FileName);

        var start = new ProcessStartInfo(tool.FileName)
        {
            UseShellExecute = tool.Shell,
            WorkingDirectory = File.Exists(tool.FileName) ? Path.GetDirectoryName(tool.FileName)! : Environment.CurrentDirectory
        };
        foreach (var argument in tool.Arguments) start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    private static SystemToolShortcut Create(string key)
    {
        var tool = Resolve(key);
        var available = tool.Shell || File.Exists(tool.FileName);
        var tooltip = available ? tool.Title : $"{tool.Title}: средство отсутствует в этой редакции Windows";
        return new(tool.Key, tool.Title, tool.Icon, available, tooltip);
    }

    private static Tool Resolve(string key) => key switch
    {
        "computer" => new(key, "Управление компьютером", "\uE7F4", Path.Combine(System, "mmc.exe"), [Path.Combine(System, "compmgmt.msc")]),
        "policy" => new(key, "Групповые политики", "\uE8D4", Path.Combine(System, "mmc.exe"), [Path.Combine(System, "gpedit.msc")]),
        "terminal" => Terminal(),
        "control" => new(key, "Панель управления", "\uE713", Path.Combine(System, "control.exe"), []),
        "settings" => new(key, "Параметры Windows", "\uE713", Path.Combine(Windows, "ImmersiveControlPanel", "SystemSettings.exe"), []),
        "adapters" => new(key, "Сетевые адаптеры", "\uE774", Path.Combine(System, "control.exe"), ["ncpa.cpl"]),
        "info" => new(key, "Сведения о системе", "\uE9D9", Path.Combine(System, "msinfo32.exe"), []),
        "security" => new(key, "Безопасность Windows", "\uE72E", "windowsdefender:", [], true),
        _ => throw new ArgumentOutOfRangeException(nameof(key), "Неизвестная системная команда.")
    };

    private static Tool Terminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(alias)) return new("terminal", "Терминал", "\uE756", alias, ["-w", "new"]);
        var powershell = Path.Combine(Windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return new("terminal", "Терминал PowerShell", "\uE756", powershell, ["-NoLogo", "-NoExit"]);
    }
}
