using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ITSeti.Maintenance.Win7
{
    internal static class LegacyProcessTrust
    {
        internal static void Inspect(string path, out string signature, out string signer)
        {
            signature = "Unknown";
            signer = "";
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            var file = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
            var name = Marshal.StringToCoTaskMemUni(path);
            var data = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustData)));
            var opened = false;
            try
            {
                Marshal.StructureToPtr(new FileInfo { Size=(uint)Marshal.SizeOf(typeof(FileInfo)),Path=name },file,false);
                var trust = new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)),UiChoice=2,UnionChoice=1,
                    File=file,StateAction=1,ProviderFlags=0x10 | 0x1000 };
                Marshal.StructureToPtr(trust,data,false);
                opened = true;
                var result = WinVerifyTrust(new IntPtr(-1),ref action,data);
                signature = result == 0 ? "Valid" : result == 0x800B0100 ? "NotSigned" : "NotTrusted";
                if (result == 0)
                {
                    using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                        signer = certificate.GetNameInfo(X509NameType.SimpleName,false);
                }
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException ||
                ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is DllNotFoundException || ex is EntryPointNotFoundException)
            { /* Missing signer information must not hide an executable. */ }
            finally
            {
                if (opened)
                {
                    var trust = (TrustData)Marshal.PtrToStructure(data,typeof(TrustData));
                    trust.StateAction=2;
                    Marshal.StructureToPtr(trust,data,false);
                    WinVerifyTrust(new IntPtr(-1),ref action,data);
                }
                Marshal.FreeHGlobal(data);
                Marshal.FreeHGlobal(file);
                Marshal.FreeCoTaskMem(name);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInfo { public uint Size; public IntPtr Path,Handle,Subject; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint Size; public IntPtr Callback,Sip; public uint UiChoice,Revocation,UnionChoice;
            public IntPtr File; public uint StateAction; public IntPtr State,Url; public uint ProviderFlags,UiContext;
        }
        [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)]
        private static extern uint WinVerifyTrust(IntPtr window,ref Guid action,IntPtr data);
    }
}
