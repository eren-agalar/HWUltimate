using System.Runtime.InteropServices;
using System.Windows;

namespace HWUltimate.App.Services;

public static class ClipboardService
{
    /// <summary>Sets clipboard text, retrying briefly because another process may hold the clipboard open.</summary>
    public static bool SetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }
}
