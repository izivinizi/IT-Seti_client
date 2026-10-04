using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ITSeti.Maintenance.Infrastructure;

public static class OrganizationSetupRunner
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private sealed record Package(string FileName, string Sha256);
    private static readonly IReadOnlyDictionary<string, Package> Packages = new Dictionary<string, Package>(StringComparer.Ordinal)
    {
        ["AnyDesk"] = new("AnyDesk-installer.exe", "B9AD79EAF7A4133F95F24C3B9D976C72F34264DC5C99030F0E57992CB5621F78"),
        ["RMS"] = new("Host-IT-SETI.RMS.7.7.3.0v3.msi", "9A8E4096C155B7432BDC35F5079169B4F496D4E4A0AAC943DA44B5E877E27ABF"),
        ["OCS"] = new("OCS-Agent-Installerv4.exe", "EA1239BD75C43A85F89BD5813B64D9D0789F8E598EA24F29B2B28CE1F012ED3D"),
        ["Panel"] = new("DesktopInfo3230.exe", "BE653BF81088855640BD7F2D42D682BCB2731C71F390A83928D68AB6E707021C")
    };
    private static readonly IReadOnlyDictionary<string, Package> BundledPackages = new Dictionary<string, Package>(StringComparer.Ordinal)
    {
        ["WinRAR"] = new("winrar-x64-723ru.exe", "831EE7523E1D9D542DDA1531E88D9F229312F2392CD7A99F9B9C885AB70604F2"),
        ["Yandex"] = new("Yandex.exe", "E761361E28510C954A7F60A49E625EE1ECCC318CA1D0F05D7255A05E3E7DF4AF")
    };
    private static readonly IReadOnlyDictionary<string, string> PanelFileHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DesktopInfo.ini"] = "8DC7F238E102200846E193B61A3EF53B592B0396190562E823F571BD0F94EEF8",
        ["update-support-ids.ps1"] = "61492DCC75D988778E12925C8CB0F4867C15BEC8B022E6EEA31C9B5BBE7C8617",
        ["start-panel.vbs"] = "BBB7894DFEF424A161BCEE4C058AFA64B7C949B5C0074DC937CE56476932C8B5"
    };
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public static async Task<IReadOnlyList<string>> CheckAsync(string? component = null)
    {
        var problems = new List<string>();
        if (File.Exists(Path.Combine(MaintenanceRoot,"server-device.json")))
        {
            var catalog = ServerSoftwareCatalog.Read();
            if (catalog.Count == 0) return ["Каталог ПО сервера ещё не получен. Выполните проверку ПК и повторите установку."];
            if (component is not null && !catalog.Any(x => x.Key.Equals(component,StringComparison.OrdinalIgnoreCase)))
                return ["Выбранного компонента нет в актуальном каталоге сервера."];
            return [];
        }
        if (component is not null && BundledPackages.TryGetValue(component, out var bundled))
        {
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "Tools", "Software", bundled.FileName);
            if (!File.Exists(bundledPath)) return [$"В комплекте приложения нет {bundled.FileName}."];
            using var bundledStream = File.OpenRead(bundledPath);
            var bundledHash = Convert.ToHexString(await SHA256.HashDataAsync(bundledStream));
            if (!bundledHash.Equals(bundled.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(await Task.Run(() => VerifyAuthenticode(bundledPath)), "Valid", StringComparison.Ordinal))
                problems.Add($"{bundled.FileName}: неверный хеш или подпись установщика.");
            return problems;
        }
        if (component is not null && !Packages.ContainsKey(component)) return ["Неизвестный компонент установки."];
        var bundledDirectory = OrganizationSoftwareAudit.FindBundledDirectory();
        if (string.IsNullOrWhiteSpace(bundledDirectory)) return ["Встроенный комплект ПО ИТ-Сети не найден."];
        var selectedPackages = component is null ? Packages : Packages.Where(item => item.Key == component)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        foreach (var (key, package) in selectedPackages)
        {
            var path = Path.Combine(bundledDirectory, "system", "packages", package.FileName);
            if (!File.Exists(path)) { problems.Add($"Нет пакета {package.FileName}."); continue; }
            var signature = await Task.Run(() => VerifyAuthenticode(path));
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!hash.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{package.FileName}: SHA-256 не совпадает с проверенным комплектом (подпись: {signature}).");
        }

        if (component is null or "Panel")
        {
            foreach (var (name, expectedHash) in PanelFileHashes)
            {
                var path = Path.Combine(bundledDirectory, "system", "panel", name);
                if (!File.Exists(path)) { problems.Add($"Нет файла панели {name}."); continue; }
                var bytes = await File.ReadAllBytesAsync(path);
                var normalized = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"));
                var hash = Convert.ToHexString(SHA256.HashData(normalized));
                if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"Файл панели {name} изменён после проверки.");
            }
        }
        return problems;
    }

    private static string VerifyAuthenticode(string path)
    {
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
        var actionId = GenericVerifyV2;
        var verificationStarted = false;
        try
        {
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer
            }, filePointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = filePointer,
                StateAction = 1,
                ProviderFlags = 0x10 | 0x1000
            };
            Marshal.StructureToPtr(data, dataPointer, false);
            verificationStarted = true;
            var status = WinVerifyTrust(new IntPtr(-1), ref actionId, dataPointer);
            return status switch
            {
                0 => "Valid",
                0x800B0100 => "NotSigned",
                0x80096010 => "HashMismatch",
                0x800B0109 => "NotTrusted",
                0x800B0101 => "Expired",
                _ => $"UnknownError (0x{status:X8})"
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return "проверка Windows недоступна: " + ex.Message;
        }
        finally
        {
            if (verificationStarted)
            {
                var data = Marshal.PtrToStructure<WinTrustData>(dataPointer);
                data.StateAction = 2;
                Marshal.StructureToPtr(data, dataPointer, false);
                try { _ = WinVerifyTrust(new IntPtr(-1), ref actionId, dataPointer); }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            }
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true, SetLastError = false)]
    private static extern uint WinVerifyTrust(IntPtr window, ref Guid actionId, IntPtr trustData);

    private const string InstallTask = "ITSeti-Maintenance-OrganizationSetup";
    private static readonly string MaintenanceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSeti", "Maintenance");

    public static async Task<int> RunBaseAsync(IProgress<string>? progress = null)
        => await RunAsync(null, "Install", progress);

    public static async Task<int> RunComponentAsync(string component, IProgress<string>? progress = null)
        => await RunAsync(component, "Install", progress);

    public static async Task<int> RunBundledComponentAsync(string component, IProgress<string>? progress = null)
    {
        if (!BundledPackages.ContainsKey(component)) throw new ArgumentException("Неизвестный компонент приложения.", nameof(component));
        return await RunAsync(component, "Install", progress);
    }

    public static async Task<int> RunUninstallComponentAsync(string component, IProgress<string>? progress = null)
        => await RunAsync(component, "Uninstall", progress);

    private static async Task<int> RunAsync(string? component, string operation, IProgress<string>? progress)
    {
        await RunLock.WaitAsync();
        try
        {
        if (component is null && operation != "Install") throw new InvalidOperationException("Для действия требуется выбрать компонент.");
        if (operation == "Install")
        {
            if (!File.Exists(Path.Combine(MaintenanceRoot,"server-device.json")) && (component is null || !BundledPackages.ContainsKey(component)))
            {
                if (OrganizationSoftwareAudit.FindBundledDirectory() is null)
                throw new InvalidOperationException("Встроенный комплект ПО ИТ-Сети не найден.");
            }
            var problems = await CheckAsync(component);
            if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        }
        var requestRoot = Path.Combine(MaintenanceRoot, "OrganizationSetupRequests");
        var resultRoot = Path.Combine(MaintenanceRoot, "OrganizationSetupRuns");
        if (!Directory.Exists(requestRoot) || !Directory.Exists(resultRoot))
            throw new InvalidOperationException("Системная установка не настроена. Переустановите приложение от администратора.");

        var id = Guid.NewGuid().ToString("N");
        var request = Path.Combine(requestRoot, id + ".json");
        var result = Path.Combine(resultRoot, id, "result.txt");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { Component = component, Operation = operation }), new UTF8Encoding(false));
        try
        {
            using var task = new Process
            {
                StartInfo = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "/Run", "/TN", InstallTask }
                }
            };
            try { task.Start(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Не удалось запустить установленную системную задачу.", ex);
            }
            await task.WaitForExitAsync();
            if (task.ExitCode != 0)
                throw new InvalidOperationException($"Не удалось запустить системную установку (код {task.ExitCode}). Проверьте регистрацию задачи ITSeti-Maintenance-OrganizationSetup.");

            progress?.Report("Системная установка запущена. Проверяем подписанные пакеты…");
            var deadline = DateTime.UtcNow.AddMinutes(90);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(result))
                {
                    var status = (await File.ReadAllTextAsync(result)).Trim();
                    if (status == "OK") return 0;
                    throw new InvalidOperationException(status.Length > 0 ? status : "Установка завершилась без результата.");
                }
                await Task.Delay(1500);
            }
            throw new TimeoutException("Системная установка не завершилась за 90 минут.");
        }
        finally
        {
            try { File.Delete(request); } catch (IOException) { }
        }
        }
        finally { RunLock.Release(); }
    }
}
