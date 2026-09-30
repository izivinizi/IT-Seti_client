using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ITSeti.Maintenance.App;

internal static class WindowsAdminAccountDiscovery
{
    private const int ErrorMoreData = 234;

    public static IReadOnlyList<string> FindCandidates()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            var group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount)).Value.Split('\\').Last();
            var localMachine = Environment.MachineName;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resume = 0;
            int status;
            do
            {
                status = NetLocalGroupGetMembers(null, group, 2, out var buffer, -1,
                    out var entries, out _, ref resume);
                try
                {
                    if (status != 0 && status != ErrorMoreData) break;
                    var size = Marshal.SizeOf<LocalGroupMemberInfo2>();
                    for (var i = 0; i < entries; i++)
                    {
                        var item = Marshal.PtrToStructure<LocalGroupMemberInfo2>(buffer + i * size);
                        if (item.SidUsage != 1) continue;
                        var account = Marshal.PtrToStringUni(item.DomainAndName);
                        if (account is not null && account.StartsWith(localMachine + "\\", StringComparison.OrdinalIgnoreCase))
                            names.Add(account);
                    }
                }
                finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
            } while (status == ErrorMoreData);
            return names.OrderBy(AccountPriority).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static int AccountPriority(string account)
    {
        var name = account[(account.LastIndexOf('\\') + 1)..];
        if (name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("it-seti", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("user", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMemberInfo2
    {
        public IntPtr Sid;
        public int SidUsage;
        public IntPtr DomainAndName;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string groupName, int level,
        out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
