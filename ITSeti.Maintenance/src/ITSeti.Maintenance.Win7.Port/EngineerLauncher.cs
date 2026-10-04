using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace ITSeti.Maintenance.App
{
    internal static class EngineerLauncher
    {
        public static void Start(string account, string password, string originalUserSid)
        {
            var value = account.Trim();
            var slash = value.LastIndexOf('\\');
            if (value.Length == 0 || slash == 0 || slash == value.Length - 1)
                throw new ArgumentException("Укажите имя пользователя, ПК\\пользователь или ДОМЕН\\пользователь.");
            var domain = slash < 0 ? Environment.MachineName : value.Substring(0, slash);
            var user = slash < 0 ? value : value.Substring(slash + 1);
            if (domain == ".") domain = Environment.MachineName;
            if (user.Length == 0) throw new ArgumentException("Укажите учётную запись Windows.");

            using (var secure = new SecureString())
            {
                foreach (var c in password) secure.AppendChar(c);
                secure.MakeReadOnly();
                var start = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ITSeti.Maintenance.Win7.Port.exe"))
                {
                    Arguments = "--engineer-bootstrap --cleanup-sid \"" + (originalUserSid ?? "") + "\"",
                    UserName = user,
                    Domain = domain,
                    Password = secure,
                    LoadUserProfile = true,
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory
                };
                try
                {
                    using (var process = Process.Start(start))
                        if (process == null) throw new InvalidOperationException("Windows не запустила инженерский процесс.");
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1326) throw new InvalidOperationException("Неверное имя пользователя или пароль.", ex);
                    throw new InvalidOperationException("Windows не запустила инженерский процесс (код " + ex.NativeErrorCode + ").", ex);
                }
            }
        }

        public static void Bootstrap(string originalUserSid)
        {
            var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ITSeti.Maintenance.Win7.Port.exe");
            using (var process = Process.Start(new ProcessStartInfo(exe,
                "--engineer --cleanup-sid \"" + (originalUserSid ?? "") + "\"")
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory }))
                if (process == null) throw new InvalidOperationException("Windows не открыла инженерское окно.");
        }
    }
}
