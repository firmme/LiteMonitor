using System.Diagnostics;
using LiteMonitor.src.WebServer;

namespace LiteMonitor.src.Core.Actions;

public static class WebActions
{
    public static void OpenWebMonitor()
    {
        if (LiteWebServer.Instance?.Address is not { } address) return;
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    public static void CopyWebAddress()
    {
        if (LiteWebServer.Instance?.Address is not { } address) return;
        try { Clipboard.SetText(address.AbsoluteUri); }
        catch (Exception ex) { ShowError(ex); }
    }

    private static void ShowError(Exception error) =>
        MessageBox.Show(error.Message, LanguageManager.T("Menu.WebServer"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
