using System.ComponentModel;
using System.Runtime.InteropServices;
using ITSeti.Maintenance.Infrastructure;

namespace ITSeti.Maintenance.Service;

internal static class WtsTicketNotification
{
    private const int WtsActive = 0;
    private const int InformationIcon = 0x00000040;
    private const int TopMost = 0x00040000;
    private const uint NotificationTimeoutSeconds = 15;

    public static void Show(IReadOnlyList<TicketUpdateMessage> messages)
    {
        if (messages.Count == 0 || !WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var sessions, out var count)) return;
        try
        {
            const string title = "ИТ-Сети: ответ по заявке";
            var latestTitle = CleanTitle(messages[^1].Title);
            var text = messages.Count == 1
                ? "Инженер ответил по заявке «" + latestTitle + "»."
                : $"Новых ответов по заявкам: {messages.Count}. Последний ответ: «{latestTitle}».";
            var recordSize = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0u; index < count; index++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessions, checked((int)(index * (uint)recordSize))));
                if (session.SessionId == 0 || session.State != WtsActive) continue;
                if (!WTSSendMessageW(IntPtr.Zero, session.SessionId, title, checked((uint)(title.Length * sizeof(char))),
                    text, checked((uint)(text.Length * sizeof(char))), InformationIcon | TopMost,
                    NotificationTimeoutSeconds, out _, false))
                {
                    ApplicationErrorStore.Record("Заявки", "Показ уведомления в пользовательской сессии",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
        }
        finally { WTSFreeMemory(sessions); }
    }

    private static string CleanTitle(string title)
    {
        var safe = string.Join(' ', new string(title.Select(character => char.IsControl(character) ? ' ' : character).ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return safe.Length <= 120 ? safe : safe[..120].TrimEnd() + "…";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version,
        out IntPtr sessionInfo, out uint count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSSendMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSSendMessageW(IntPtr server, uint sessionId, string title, uint titleLength,
        string message, uint messageLength, int style, uint timeout, out uint response,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
