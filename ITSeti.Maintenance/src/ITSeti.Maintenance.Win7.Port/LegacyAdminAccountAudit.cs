using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ITSeti.Maintenance.App
{
    internal static class LegacyAdminAccountAudit
    {
        public static string Inspect()
        {
            try
            {
                var group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                    .Translate(typeof(NTAccount)).Value.Split('\\').Last();
                var names = new List<string>();
                var resume = 0;
                int status;
                do
                {
                    IntPtr buffer;
                    int entries;
                    int total;
                    status = NetLocalGroupGetMembers(null, group, 2, out buffer, -1, out entries, out total, ref resume);
                    try
                    {
                        if (status != 0 && status != 234)
                            return "Не удалось проверить локальных администраторов (код " + status + ").";
                        for (var i = 0; i < entries; i++)
                        {
                            var item = (MemberInfo)Marshal.PtrToStructure(IntPtr.Add(buffer, i * Marshal.SizeOf(typeof(MemberInfo))), typeof(MemberInfo));
                            if (item.SidUsage == 1 || item.SidUsage == 2)
                            {
                                var name = Marshal.PtrToStringUni(item.DomainAndName);
                                if (!string.IsNullOrWhiteSpace(name) && !IsAllowedLocalAccount(name)) names.Add(name);
                            }
                        }
                    }
                    finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
                } while (status == 234);
                var unexpected = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
                return unexpected.Length == 0
                    ? "Локальные администраторы: без замечаний."
                    : "Посторонние администраторы: " + string.Join(", ", unexpected);
            }
            catch (Exception ex) when (ex is SystemException || ex is ExternalException)
            {
                return "Не удалось проверить локальных администраторов.";
            }
        }

        private static bool IsAllowedLocalAccount(string account)
        {
            var slash = account.LastIndexOf('\\');
            if (slash < 0 || !account.Substring(0, slash).Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return false;
            var name = account.Substring(slash + 1);
            return name.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("it-seti", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Администратор", StringComparison.OrdinalIgnoreCase);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemberInfo
        {
            public IntPtr Sid;
            public int SidUsage;
            public IntPtr DomainAndName;
        }

        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetLocalGroupGetMembers(string serverName, string groupName, int level,
            out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

        [DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr buffer);
    }
}
