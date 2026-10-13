using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTests.Execute();
        if (!IsAdministrator()) {
            try {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, Array.IndexOf(args, "--tray") >= 0 ? "--tray" : "") { UseShellExecute = true, Verb = "runas" });
                return 0;
            } catch (System.ComponentModel.Win32Exception) { /* Keep settings available if UAC is declined. */ }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string identity;
        using (var user = WindowsIdentity.GetCurrent()) identity = user.User.Value;
        bool created;
        using (var instance = new System.Threading.Mutex(true, "Local\\AutoHello-" + identity + (IsAdministrator() ? "-admin" : "-standard"), out created)) {
            if (!created) {
                if (Array.IndexOf(args, "--tray") < 0) MessageBox.Show("AutoHello is already running. Open its tray icon to change settings.", "AutoHello");
                return 0;
            }
            try { Application.Run(new MainForm(Array.IndexOf(args, "--tray") >= 0)); }
            finally { instance.ReleaseMutex(); }
        }
        return 0;
    }

    internal static bool IsAdministrator()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
