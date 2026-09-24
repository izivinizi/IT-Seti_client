using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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

    public static async Task<int> RunBaseAsync(string source)
    {
        var problems = await CheckAsync(source);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        var script = Path.Combine(source, "system", "Install.ps1");
        var command = "& '" + script.Replace("'", "''") + "' -Silent; exit $LASTEXITCODE";
        using var run = new Process { StartInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command)),
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
        } };
        try { run.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Повышение прав не подтверждено: " + ex.Message, ex); }
        await run.WaitForExitAsync();
        return run.ExitCode;
    }
}
