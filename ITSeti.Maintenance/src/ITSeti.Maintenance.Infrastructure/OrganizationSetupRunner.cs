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
    private const string AuditedScriptHash = "D15BA9507807B6B27094634952A59F3204D954AC6EB88F5180B0BCECE062355D";
    private static readonly string[] Packages =
    [
        "AnyDesk-installer.exe", "Host-IT-SETI.RMS.7.7.3.0v3.msi",
        "OCS-Agent-Installerv4.exe", "DesktopInfo3230.exe"
    ];

    public static async Task<IReadOnlyList<string>> CheckAsync(string? source)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(source)) return ["Комплект ITSETI-Setup не найден."];
        var script = Path.Combine(source, "system", "Install.ps1");
        if (!File.Exists(script)) return ["В комплекте нет system\\Install.ps1."];
        using (var stream = File.OpenRead(script))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!hash.Equals(AuditedScriptHash, StringComparison.OrdinalIgnoreCase))
                problems.Add("Скрипт комплекта изменён после ревизии; требуется повторная проверка.");
        }
        var existing = new List<(string Name, string Path)>();
        foreach (var package in Packages)
        {
            var path = Path.Combine(source, "system", "packages", package);
            if (!File.Exists(path)) { problems.Add($"Нет пакета {package}."); continue; }
            existing.Add((package, path));
        }
        if (existing.Count == 0) return problems;

        var signatures = await Task.Run(() => existing.Select(package =>
            (package.Name, State: VerifyAuthenticode(package.Path))).ToArray());
        foreach (var (name, state) in signatures)
        {
            if (state != "Valid") problems.Add($"{name}: подпись {state} (нужна действительная цифровая подпись).");
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

    public static async Task<int> RunBaseAsync(string source, IProgress<string>? progress = null)
    {
        var problems = await CheckAsync(source);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        var requestRoot = Path.Combine(MaintenanceRoot, "OrganizationSetupRequests");
        var resultRoot = Path.Combine(MaintenanceRoot, "OrganizationSetupRuns");
        if (!Directory.Exists(requestRoot) || !Directory.Exists(resultRoot))
            throw new InvalidOperationException("Системная установка не настроена. Переустановите приложение от администратора.");

        var id = Guid.NewGuid().ToString("N");
        var request = Path.Combine(requestRoot, id + ".json");
        var result = Path.Combine(resultRoot, id, "result.txt");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { Source = Path.GetFullPath(source) }), new UTF8Encoding(false));
        try
        {
            using var task = new Process
            {
                StartInfo = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "/Run", "/TN", InstallTask }
                }
            };
            try { task.Start(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("Не удалось запустить установленную системную задачу.", ex);
            }
            var output = task.StandardOutput.ReadToEndAsync();
            var errors = task.StandardError.ReadToEndAsync();
            await task.WaitForExitAsync();
            if (task.ExitCode != 0)
                throw new InvalidOperationException($"Не удалось запустить системную установку: {(await errors).Trim()} {(await output).Trim()}");

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
}
