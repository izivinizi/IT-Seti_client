using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ITSeti.Maintenance.App;

internal sealed record AdminAccountAudit(string Status, IReadOnlyList<string> UnexpectedAccounts);

internal static class WindowsAdminAccountDiscovery
{
    private const int ErrorMoreData = 234;

    public static IReadOnlyList<string> FindCandidates() => ReadMembers()
        .Members.Where(member => member.SidUsage == 1 && !string.IsNullOrWhiteSpace(member.Name))
        .Select(member => member.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(AccountPriority)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static AdminAccountAudit InspectAdministrators()
    {
        var result = ReadMembers();
        if (result.Status != 0)
            return new($"Не удалось проверить состав локальной группы администраторов (код {result.Status}).", []);

        var unexpected = result.Members
            .Where(member => member.SidUsage is 1 or 2 && !IsDesignatedLocalAdmin(member.Name))
            .Select(member => member.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(unexpected.Length == 0
            ? "Локальные администраторы: без замечаний."
            : "Посторонние администраторы: " + string.Join(", ", unexpected), unexpected);
    }

    public static bool IsCurrentAccountUnexpectedAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (IsDesignatedLocalAdmin(identity.Name)) return false;
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(administratorsSid) || identity.Groups?.Contains(administratorsSid) == true;
        }
        catch { return false; }
    }

    private static bool IsDesignatedLocalAdmin(string account)
    {
        var separator = account.LastIndexOf('\\');
        if (separator < 0) return false;
        var name = account[(separator + 1)..];
        // Built-in administrator accounts are expected on both local and domain installations.
        if (name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Администратор", StringComparison.OrdinalIgnoreCase)) return true;
        if (!account[..separator].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
        return name.Equals("Admin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("it-seti", StringComparison.OrdinalIgnoreCase)
            ;
    }

    private static (int Status, List<Member> Members) ReadMembers()
    {
        var members = new List<Member>();
        if (!OperatingSystem.IsWindows()) return (50, members);
        try
        {
            var group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount)).Value.Split('\\').Last();
            var resume = 0;
            var status = 0;
            do
            {
                status = NetLocalGroupGetMembers(null, group, 2, out var buffer, -1,
                    out var entries, out _, ref resume);
                try
                {
                    if (status != 0 && status != ErrorMoreData) return (status, members);
                    var size = Marshal.SizeOf<LocalGroupMemberInfo2>();
                    for (var i = 0; i < entries; i++)
                    {
                        var item = Marshal.PtrToStructure<LocalGroupMemberInfo2>(buffer + i * size);
                        var name = Marshal.PtrToStringUni(item.DomainAndName);
                        if (!string.IsNullOrWhiteSpace(name)) members.Add(new(name, item.SidUsage));
                    }
                }
                finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
            } while (status == ErrorMoreData);
            return (status, members);
        }
        catch { return (1, members); }
    }

    private static int AccountPriority(string account)
    {
        var name = account[(account.LastIndexOf('\\') + 1)..];
        if (name.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("Administrator", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("it-seti", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("user", StringComparison.OrdinalIgnoreCase)) return 3;
        return 4;
    }

    private sealed record Member(string Name, int SidUsage);

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
