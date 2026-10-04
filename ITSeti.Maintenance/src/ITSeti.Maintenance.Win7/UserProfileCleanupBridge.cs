using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ITSeti.Maintenance.Win7
{
    internal sealed class UserProfileCleanupBridge : IDisposable
    {
        private readonly string sid;
        private readonly string pipeName;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object cleanupGate = new object();
        private CancellationTokenSource activeCleanup;

        private UserProfileCleanupBridge(string userSid)
        {
            sid = userSid;
            pipeName = GetPipeName(userSid);
            Task.Run((Action)ServeCleanup);
            Task.Run((Action)ServeCancellation);
        }

        public static string CurrentUserSid
        {
            get
            {
                using (var identity = WindowsIdentity.GetCurrent())
                    return identity.User == null ? null : identity.User.Value;
            }
        }

        public static UserProfileCleanupBridge Start(string userSid)
        {
            return string.IsNullOrWhiteSpace(userSid) ? null : new UserProfileCleanupBridge(userSid);
        }

        public static string Clean(string userSid, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userSid)) throw new ArgumentException("Не задан исходный SID пользователя.", "userSid");
            var name = GetPipeName(userSid);
            using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None))
            {
                client.Connect(10000);
                using (var reader = new StreamReader(client, Encoding.UTF8, false, 1024, true))
                using (var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                {
                    writer.WriteLine("clean");
                    var responseTask = Task.Run(() => reader.ReadLine());
                    while (!responseTask.Wait(250))
                    {
                        if (!cancellationToken.IsCancellationRequested) continue;
                        SendCancel(name);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var response = responseTask.Result;
                    if (string.IsNullOrEmpty(response)) throw new IOException("Пользовательский процесс закрыл канал очистки.");
                    if (!response.StartsWith("OK:", StringComparison.Ordinal))
                        throw new InvalidOperationException(Decode(response.StartsWith("ERROR:", StringComparison.Ordinal) ? response.Substring(6) : response));
                    return Decode(response.Substring(3));
                }
            }
        }

        public static string ActiveConsoleUserSid()
        {
            try
            {
                using (var search = new System.Management.ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
                using (var values = search.Get())
                {
                    foreach (System.Management.ManagementObject item in values)
                    {
                        var account = Convert.ToString(item["UserName"]);
                        if (string.IsNullOrWhiteSpace(account)) return null;
                        var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
                        return sid.Value;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Management.ManagementException || ex is IdentityNotMappedException ||
                                       ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { }
            return null;
        }

        private void ServeCleanup()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var server = CreateServer(pipeName))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true))
                        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            if (!string.Equals(reader.ReadLine(), "clean", StringComparison.Ordinal))
                            {
                                writer.WriteLine("ERROR:" + Encode("Неизвестная команда."));
                                continue;
                            }
                            string response;
                            using (var cancellation = new CancellationTokenSource())
                            {
                                lock (cleanupGate) activeCleanup = cancellation;
                                try { response = "OK:" + Encode(LegacyMaintenance.CleanCurrentUser(cancellation.Token)); }
                                catch (OperationCanceledException) { response = "ERROR:" + Encode("Очистка профиля отменена."); }
                                catch (Exception ex) { response = "ERROR:" + Encode(ex.Message); }
                                finally { lock (cleanupGate) activeCleanup = null; }
                            }
                            writer.WriteLine(response);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    if (!stop.IsCancellationRequested) Thread.Sleep(200);
                }
            }
        }

        private void ServeCancellation()
        {
            var cancelName = pipeName + "Cancel";
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using (var server = CreateServer(cancelName))
                    {
                        server.WaitForConnection();
                        using (var reader = new StreamReader(server, Encoding.UTF8, false, 256, true))
                            if (string.Equals(reader.ReadLine(), "cancel", StringComparison.Ordinal))
                                lock (cleanupGate) if (activeCleanup != null) activeCleanup.Cancel();
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    if (!stop.IsCancellationRequested) Thread.Sleep(200);
                }
            }
        }

        private static NamedPipeServerStream CreateServer(string name)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(CurrentUserSid),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, security);
        }

        private static void SendCancel(string name)
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", name + "Cancel", PipeDirection.InOut))
                {
                    client.Connect(2000);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false), 128, true) { AutoFlush = true })
                        writer.WriteLine("cancel");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is UnauthorizedAccessException) { }
        }

        private static string GetPipeName(string userSid)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(userSid));
                return "ITSetiWin7Cleanup" + BitConverter.ToString(hash, 0, 12).Replace("-", "");
            }
        }

        private static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "")); }
        private static string Decode(string value) { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }

        public void Dispose()
        {
            if (stop.IsCancellationRequested) return;
            stop.Cancel();
            Wake(pipeName);
            Wake(pipeName + "Cancel");
            stop.Dispose();
        }

        private static void Wake(string name)
        {
            try { using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut)) client.Connect(100); }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is UnauthorizedAccessException) { }
        }
    }
}
