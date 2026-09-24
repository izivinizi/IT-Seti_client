using System.Runtime.InteropServices;
using System.Windows;

namespace ITSeti.Maintenance.App;

internal static class ClipboardHelper
{
    public static void Copy(string value)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Clipboard.SetText(value); return; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }
}
