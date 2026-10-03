using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ITSeti.Maintenance.Win7
{
    internal static class SmartTaskInstaller
    {
        public static void Register()
        {
            RequireElevatedInstall();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ITSetiMaintenanceWin7");
            Directory.CreateDirectory(folder);
            if ((new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Каталог результатов SMART не должен быть ссылкой.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(admins);
            const InheritanceFlags children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, children, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(acl);
            foreach (var name in new[] { "smart", "benchmark" })
            {
                foreach (var suffix in new[] { "-cancel.txt", "-stopped.txt" })
                {
                    var cancellationFile = Path.Combine(folder, name + suffix);
                    if (!File.Exists(cancellationFile)) File.WriteAllText(cancellationFile, "");
                    var cancellationAcl = new FileInfo(cancellationFile).GetAccessControl();
                    cancellationAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
                    new FileInfo(cancellationFile).SetAccessControl(cancellationAcl);
                }
            }
            var inventoryDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ITSeti", "Maintenance");
            Directory.CreateDirectory(inventoryDirectory);
            var inventory = Path.Combine(inventoryDirectory, "inventory.txt");
            if (!File.Exists(inventory)) File.WriteAllText(inventory, "");
            var inventoryAcl = new FileInfo(inventory).GetAccessControl();
            inventoryAcl.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(inventory).SetAccessControl(inventoryAcl);
            var oldReport = Path.Combine(folder, "smart.json");
            if (File.Exists(oldReport)) File.Delete(oldReport);
            var oldBenchmark = Path.Combine(folder, "benchmark.json");
            if (File.Exists(oldBenchmark)) File.Delete(oldBenchmark);

            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.Connect();
            dynamic root = service.GetFolder("\\");
            RegisterTask(service, root, SmartTaskRunner.TaskName, "--collect-smart", "PT1M", "Сбор SMART");
            RegisterTask(service, root, SmartTaskRunner.ScheduledTaskName, "--collect-smart-scheduled", "PT1M", "Плановый сбор SMART");
            RegisterTask(service, root, BenchmarkTaskRunner.TaskName, "--collect-benchmark", "PT5M", "Тест системного диска DiskSpd");
            RegisterTask(service, root, BenchmarkTaskRunner.ScheduledTaskName, "--collect-benchmark-scheduled", "PT5M", "Плановый тест системного диска DiskSpd");
            RegisterTask(service, root, ScheduledCheckRunner.TaskName, "--scheduled-check", "PT30M", "Плановая быстрая проверка раз в 14 дней", 14);
            RegisterTask(service, root, ScheduledCheckRunner.FullTaskName, "--scheduled-full", "PT2H", "Плановая полная проверка и SFC раз в 60 дней", 60);
            RegisterTask(service, root, "ITSeti-Maintenance-Win7-Update", "--update-application", "PT20M", "Фоновая проверка и установка обновления приложения");
        }

        private static void RegisterTask(dynamic service, dynamic root, string name, string arguments, string timeLimit, string description, int intervalDays = 0)
        {
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "ИТ-Сети Windows 7: " + description;
            task.Settings.Enabled = true;
            task.Settings.AllowDemandStart = true;
            task.Settings.Hidden = true;
            task.Settings.ExecutionTimeLimit = timeLimit;
            task.Settings.MultipleInstances = 2;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            if (intervalDays > 0)
            {
                task.Settings.StartWhenAvailable = true;
                var start = DateTime.Today.AddDays(1).AddHours(3).ToString("yyyy-MM-ddTHH:mm:ss");
                try
                {
                    dynamic existing = root.GetTask(name);
                    var previous = Convert.ToString(existing.Definition.Triggers.Item(1).StartBoundary);
                    if (!string.IsNullOrWhiteSpace(previous)) start = previous;
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x80070002) { }
                dynamic trigger = task.Triggers.Create(2);
                trigger.StartBoundary = start;
                trigger.DaysInterval = intervalDays;
            }
            task.Principal.UserId = "SYSTEM";
            task.Principal.LogonType = 5;
            task.Principal.RunLevel = 1;
            dynamic action = task.Actions.Create(0);
            action.Path = Process.GetCurrentProcess().MainModule.FileName;
            action.Arguments = arguments;
            action.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            dynamic registered = root.RegisterTaskDefinition(name, task, 6, "SYSTEM", null, 5, null);
            registered.SetSecurityDescriptor(intervalDays > 0
                ? "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;AU)"
                : "D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)", 0);
        }

        public static void Unregister()
        {
            RequireElevatedInstall();
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.Connect();
            dynamic root = service.GetFolder("\\");
            try { root.DeleteTask(SmartTaskRunner.TaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask(SmartTaskRunner.ScheduledTaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask(BenchmarkTaskRunner.TaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask(BenchmarkTaskRunner.ScheduledTaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask(ScheduledCheckRunner.TaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask(ScheduledCheckRunner.FullTaskName, 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
            try { root.DeleteTask("ITSeti-Maintenance-Win7-Update", 0); }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80070002) { }
        }

        private static void RequireElevatedInstall()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Установка задач обслуживания требует запустить установщик от администратора.");
            }
            var current = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var expected = Path.Combine(programFiles, "ITSeti Maintenance Win7");
            var expectedX86 = Path.Combine(programFilesX86, "ITSeti Maintenance Win7");
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(current, expectedX86, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Задачу SYSTEM можно зарегистрировать только для приложения в Program Files.");
        }
    }
}
