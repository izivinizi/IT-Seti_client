using System.Diagnostics;

namespace ITSeti.Maintenance.Infrastructure;

public sealed record SystemToolShortcut(string Key, string Title, string Icon, bool Available, string ToolTip);
public sealed record SystemToolLaunchSpec(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);

public static class SystemToolsLauncher
{
    private sealed record Tool(string Key, string Title, string Icon, string FileName, string[] Arguments, bool Shell = false);

    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string[] ToolKeys = ["computer", "policy", "terminal", "control", "settings", "adapters", "info", "security"];

    public static IReadOnlyList<SystemToolShortcut> GetShortcuts() => ToolKeys.Select(Create).ToArray();

    public static SystemToolLaunchSpec GetLaunchSpec(string key)
    {
        var tool = Resolve(key);
        if (tool.Shell)
        {
            var explorer = Path.Combine(Windows, "explorer.exe");
            return new(explorer, [tool.FileName], Windows);
        }
        return new(tool.FileName, tool.Arguments, Path.GetDirectoryName(tool.FileName)!);
    }

    public static Process? Launch(string key)
    {
        var tool = Resolve(key);
        if (tool.Shell)
            return Process.Start(new ProcessStartInfo(tool.FileName) { UseShellExecute = true });
        if (!File.Exists(tool.FileName) && !tool.Shell)
            throw new FileNotFoundException("Системное приложение недоступно в этой редакции Windows.", tool.FileName);

        var spec = GetLaunchSpec(key);
        var start = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = true,
            Verb = !ElevatedProcessLauncher.IsCurrentProcessElevated ? "runas" : string.Empty,
            WorkingDirectory = spec.WorkingDirectory
        };
        foreach (var argument in spec.Arguments) start.ArgumentList.Add(argument);
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
        "settings" => new(key, "Параметры Windows", "\uE713", "ms-settings:", [], true),
        "adapters" => new(key, "Сетевые адаптеры", "\uE774", Path.Combine(System, "rundll32.exe"), ["shell32.dll,Control_RunDLL", "ncpa.cpl"]),
        "info" => new(key, "Сведения о системе", "\uE9D9", Path.Combine(System, "msinfo32.exe"), []),
        "security" => new(key, "Безопасность Windows", "\uE72E", "windowsdefender:", [], true),
        _ => throw new ArgumentOutOfRangeException(nameof(key), "Неизвестная системная команда.")
    };

    private static Tool Terminal()
    {
        var powershell = Path.Combine(Windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return new("terminal", "Терминал PowerShell", "\uE756", powershell, ["-NoLogo", "-NoExit"]);
    }
}
