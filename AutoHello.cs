using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try
            {
                byte[] original = Encoding.UTF8.GetBytes("test-only-1234");
                byte[] restored = ProtectedData.Unprotect(ProtectedData.Protect(original, null, DataProtectionScope.CurrentUser), null, DataProtectionScope.CurrentUser);
                if (Encoding.UTF8.GetString(restored) != "test-only-1234") return 1;
                if (Marshal.SizeOf(typeof(Native.INPUT)) != (IntPtr.Size == 8 ? 40 : 28)) return 2;
                if (!MainForm.PinName("PIN") || !MainForm.PinName("Enter your PIN") || MainForm.PinName("Password")) return 3;
                if (!String.Equals(Native.ProcessPath((uint)Process.GetCurrentProcess().Id), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return 5;
                if (!MainForm.VerifyTrayLifecycle()) return 6;
                StartupTask.VerifyDefinition();
                SelfTests.Run();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 4; }
        }
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

internal sealed class MainForm : Form
{
    readonly TextBox pin = new TextBox { UseSystemPasswordChar = true, Width = 330, MaxLength = 127 };
    readonly CheckBox enabled = new CheckBox { Text = "Enable automatic PIN entry", AutoSize = true };
    readonly CheckBox submit = new CheckBox { Text = "Press Enter after the PIN", Checked = true, AutoSize = true };
    readonly Label status = new Label { AutoSize = false, Width = 420, Height = 65, Text = "Paused. Set your PIN, then enable automatic entry." };
    AutomationWorker worker;
    ForegroundEvents foregroundEvents;
    string lastSavedPreferences;
    int workerGeneration;
    bool disposed;
    bool stickyStatus;
    ContextMenuStrip trayMenu;
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoHello", "pin.bin");
    readonly bool administrator = Program.IsAdministrator();
    readonly CheckBox startup = new CheckBox { Text = "Start with Windows (as administrator)", AutoSize = true };
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem trayEnabled = new ToolStripMenuItem("Automatic PIN entry");
    readonly string preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoHello", "preferences.txt");
    bool loading = true;
    bool exiting;

    public MainForm(bool startInTray, bool testing = false)
    {
        Text = administrator ? "AutoHello — Administrator" : "AutoHello — Administrator required";
        ClientSize = new Size(460, 425);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Label { Text = "Preset Windows Hello PIN", AutoSize = true });
        layout.Controls.Add(pin);
        var save = new Button { Text = "Save PIN", AutoSize = true };
        save.Click += delegate { SavePin(); };
        layout.Controls.Add(save);
        layout.Controls.Add(submit);
        layout.Controls.Add(enabled);
        layout.Controls.Add(startup);
        layout.Controls.Add(new Label { Text = administrator ? "Running as administrator" : "Run as administrator to enable automatic entry and startup.", AutoSize = true });
        layout.Controls.Add(new Label { Text = "Only focused broker PIN fields qualify. Emptiness cannot be verified. Keep the field empty; do not type while enabled.", Width = 420, Height = 45 });
        layout.Controls.Add(status);
        var restart = new Button { Text = "Restart as administrator", AutoSize = true };
        restart.Enabled = !administrator;
        restart.Click += delegate { RestartAsAdministrator(); };
        layout.Controls.Add(restart);
        var forget = new Button { Text = "Forget saved PIN", AutoSize = true };
        forget.Click += delegate {
            enabled.Checked = false;
            try { if (File.Exists(settings)) File.Delete(settings); pin.Clear(); SetStatus("Saved PIN removed."); }
            catch { SetStatus("Could not remove the saved PIN file."); }
        };
        layout.Controls.Add(forget);
        Controls.Add(layout);
        enabled.Enabled = administrator;
        submit.Enabled = administrator;
        startup.Enabled = administrator;
        var menu = new ContextMenuStrip();
        trayMenu = menu;
        menu.Items.Add("Open settings", null, delegate { ShowSettings(); });
        trayEnabled.Enabled = administrator;
        trayEnabled.Click += delegate { enabled.Checked = !enabled.Checked; };
        menu.Items.Add(trayEnabled);
        var trayAdmin = menu.Items.Add("Run as administrator", null, delegate { RestartAsAdministrator(); });
        trayAdmin.Enabled = !administrator;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
        tray.Icon = SystemIcons.Shield;
        tray.ContextMenuStrip = menu;
        tray.Text = "AutoHello — Paused";
        tray.Visible = !testing;
        tray.DoubleClick += delegate { ShowSettings(); };
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        Shown += delegate { if (startInTray) Hide(); };
        enabled.CheckedChanged += delegate {
            ConfigureWorker();
            if (enabled.Checked && !administrator) { enabled.Checked = false; return; }
            if (enabled.Checked && pin.Text.Length == 0) { enabled.Checked = false; SetStatus("Enter your PIN first."); return; }
            trayEnabled.Checked = enabled.Checked;
            tray.Text = enabled.Checked ? "AutoHello — Automatic entry enabled" : "AutoHello — Paused";
            SetStatus(enabled.Checked ? "Watching for a focused Windows Hello PIN field. Use tray > Exit to stop." : "Paused.");
            stickyStatus = false;
            SavePreferences();
        };
        submit.CheckedChanged += delegate { ConfigureWorker(); SavePreferences(); };
        pin.TextChanged += delegate { enabled.Checked = false; };
        string loadError = null;
        try {
            if (!testing && File.Exists(settings)) {
                pin.Text = StoredSettings.ReadPin(settings);
            }
        } catch { loadError = "Saved PIN could not be loaded. Enter and save it again."; }
        try {
            if (!testing && File.Exists(preferences)) {
                bool savedEnabled, savedSubmit;
                StoredSettings.ReadPreferences(preferences, out savedEnabled, out savedSubmit);
                lastSavedPreferences = StoredSettings.Preferences(savedEnabled, savedSubmit);
                submit.Checked = savedSubmit;
                enabled.Checked = administrator && pin.Text.Length > 0 && savedEnabled;
            }
        } catch { enabled.Checked = false; loadError = "Could not load preferences. Review your settings."; }
        try {
            if (!testing) {
                StartupState startupState = StartupTask.GetState();
                startup.Checked = startupState == StartupState.Enabled;
                if (startupState == StartupState.DifferentExecutable) SetStatus("The startup task points to another executable. It was not changed. Check Start with Windows to replace it with this location.");
                if (startupState == StartupState.DifferentConfiguration) SetStatus("The startup task has an unexpected configuration. It was not changed. Review it in Task Scheduler.");
            }
        }
        catch { SetStatus("Could not read Windows startup settings."); }
        startup.CheckedChanged += delegate {
            if (loading) return;
            try {
                StartupTask.SetEnabled(startup.Checked);
                SetStatus(startup.Checked ? "Will start in the tray as administrator when you sign in. Keep this executable in this location." : "Windows startup disabled.");
            } catch (Exception error) {
                loading = true;
                startup.Checked = !startup.Checked;
                loading = false;
                SetStatus("Could not change startup: " + error.GetType().Name + ".");
            }
        };
        loading = testing;
        if (loadError != null) { enabled.Checked = false; SetStatus(loadError); }
        if (!administrator) SetStatus("Administrator permission is required. Click Restart as administrator.");
        if (!testing) {
            // Create the UI handle before any worker report can be posted.
            IntPtr handle = Handle;
            worker = new AutomationWorker(new WindowsAutomation(), WorkerReport, true);
            try { foregroundEvents = new ForegroundEvents(worker.SignalFocusChange); }
            catch (Exception error) { SetStatus("Foreground notifications unavailable (" + error.GetType().Name + "). Detection will use focus events and fallback checks."); }
            ConfigureWorker();
        }
    }

    internal static bool VerifyTrayLifecycle()
    {
        using (var form = new MainForm(false, true)) {
            if (form.enabled.Enabled != Program.IsAdministrator() || form.trayEnabled.Enabled != Program.IsAdministrator()) return false;
            var close = new FormClosingEventArgs(CloseReason.UserClosing, false);
            form.OnFormClosing(close);
            if (!close.Cancel) return false;
            form.exiting = true;
            close = new FormClosingEventArgs(CloseReason.UserClosing, false);
            form.OnFormClosing(close);
            if (close.Cancel) return false;
            form.OnFormClosed(new FormClosedEventArgs(CloseReason.UserClosing));
            return true;
        }
    }

    void ShowSettings() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void RestartAsAdministrator()
    {
        try {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
            exiting = true;
            Close();
        } catch (System.ComponentModel.Win32Exception) { SetStatus("Administrator restart was cancelled or unavailable."); }
    }

    void SavePreferences()
    {
        if (loading || !administrator) return;
        try {
            string value = StoredSettings.Preferences(enabled.Checked, submit.Checked);
            if (lastSavedPreferences == value) return;
            AtomicFile.Write(preferences, Encoding.UTF8.GetBytes(value));
            lastSavedPreferences = value;
        } catch { SetStatus("Could not save preferences."); }
    }

    void SavePin()
    {
        if (pin.Text.Length == 0) { SetStatus("Enter a PIN first."); return; }
        byte[] plain = Encoding.UTF8.GetBytes(pin.Text);
        try {
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(settings));
            AtomicFile.Write(settings, encrypted);
            SetStatus("PIN saved with Windows account encryption. Enable automatic entry when ready.");
        } catch { SetStatus("Could not save the PIN."); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    internal static bool PinName(string name)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"\bPIN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    void ConfigureWorker()
    {
        if (worker != null) workerGeneration = worker.Configure(administrator && enabled.Checked ? pin.Text : null, submit.Checked);
    }

    void SetStatus(string message) { stickyStatus = true; status.Text = message; }

    void WorkerReport(int version, string message, bool pause, bool durable)
    {
        try {
            BeginInvoke((Action)delegate {
                if (disposed || version != workerGeneration) return;
                if (!durable && !worker.IsLatestNotice(version, message)) return;
                if (pause) enabled.Checked = false;
                if (durable || !stickyStatus) { status.Text = message; stickyStatus = durable; }
            });
        } catch (InvalidOperationException) { /* Form handle is closing. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) {
            disposed = true; loading = true;
            if (worker != null) worker.Dispose();
            if (foregroundEvents != null) foregroundEvents.Dispose();
            tray.Visible = false; tray.Dispose();
            if (trayMenu != null) trayMenu.Dispose();
            pin.Clear();
        }
        base.Dispose(disposing);
    }
}
internal sealed class ComScope : IDisposable
{
    readonly List<object> owned = new List<object>();
    internal dynamic Own(object value) { owned.Add(value); return value; }
    public void Dispose()
    {
        for (int i = owned.Count - 1; i >= 0; i--)
            if (Marshal.IsComObject(owned[i])) Marshal.ReleaseComObject(owned[i]);
    }
}

internal enum StartupState { Disabled, Enabled, DifferentExecutable, DifferentConfiguration }

internal static class StartupTask
{
    static string TaskName { get { using (var user = WindowsIdentity.GetCurrent()) return "AutoHello-" + user.User.Value; } }

    internal static StartupState Classify(bool enabled, int type, string path, string arguments)
    {
        if (type != 0) return StartupState.DifferentConfiguration;
        if (!String.Equals(path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return StartupState.DifferentExecutable;
        if (arguments != "--tray") return StartupState.DifferentConfiguration;
        return enabled ? StartupState.Enabled : StartupState.Disabled;
    }

    internal static StartupState GetState()
    {
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            try {
                dynamic folder = com.Own(service.GetFolder("\\"));
                dynamic task = com.Own(folder.GetTask(TaskName));
                dynamic definition = com.Own(task.Definition);
                dynamic actions = com.Own(definition.Actions);
                if (actions.Count != 1) return StartupState.DifferentConfiguration;
                dynamic action = com.Own(actions.Item(1));
                if (action.Type != 0) return StartupState.DifferentConfiguration;
                return Classify((bool)task.Enabled, (int)action.Type, (string)action.Path, (string)action.Arguments);
            }
            catch (COMException error) { if ((uint)error.HResult == 0x80070002) return StartupState.Disabled; throw; }
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!Program.IsAdministrator()) throw new UnauthorizedAccessException();
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            dynamic folder = com.Own(service.GetFolder("\\"));
            if (!enabled) {
                try { folder.DeleteTask(TaskName, 0); }
                catch (COMException error) { if ((uint)error.HResult != 0x80070002) throw; }
                return;
            }
            string sid;
            using (var user = WindowsIdentity.GetCurrent()) sid = user.User.Value;
            dynamic task = CreateDefinition(service, sid, com);
            com.Own(folder.RegisterTaskDefinition(TaskName, task, 6, sid, null, 3, null));
        }
    }

    static dynamic CreateDefinition(dynamic service, string sid, ComScope com)
    {
        dynamic task = com.Own(service.NewTask(0));
        dynamic registration = com.Own(task.RegistrationInfo);
        dynamic principal = com.Own(task.Principal);
        dynamic settings = com.Own(task.Settings);
        dynamic triggers = com.Own(task.Triggers);
        dynamic actions = com.Own(task.Actions);
        registration.Description = "Start AutoHello in the tray at sign-in for this user.";
        principal.UserId = sid;
        principal.LogonType = 3; // Interactive token; no password stored.
        principal.RunLevel = 1; // Highest available privileges.
        settings.Enabled = true;
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S";
        settings.MultipleInstances = 2; // Ignore a duplicate scheduled start.
        dynamic trigger = com.Own(triggers.Create(9)); // Logon.
        trigger.UserId = sid;
        dynamic action = com.Own(actions.Create(0));
        action.Path = Application.ExecutablePath;
        action.Arguments = "--tray";
        action.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
        return task;
    }

    internal static void VerifyDefinition()
    {
        // Build a definition in memory only. Do not register or run a task.
        using (var com = new ComScope()) {
            dynamic service = com.Own(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")));
            service.Connect();
            string sid;
            using (var user = WindowsIdentity.GetCurrent()) sid = user.User.Value;
            dynamic task = CreateDefinition(service, sid, com);
            var xml = new System.Xml.XmlDocument();
            xml.LoadXml((string)task.XmlText);
            var ns = new System.Xml.XmlNamespaceManager(xml.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            if (xml.SelectSingleNode("//t:RunLevel", ns).InnerText != "HighestAvailable" ||
                xml.SelectSingleNode("//t:LogonType", ns).InnerText != "InteractiveToken" ||
                xml.SelectSingleNode("//t:LogonTrigger/t:UserId", ns).InnerText != sid ||
                xml.SelectSingleNode("//t:Exec/t:Arguments", ns).InnerText != "--tray" ||
                xml.SelectSingleNode("//t:Exec/t:Command", ns).InnerText != Application.ExecutablePath)
                throw new InvalidOperationException("Startup definition validation failed.");
        }
    }
}

internal static class Native
{
    internal static string ProcessPath(uint processId) { long created; return ProcessInfo(processId, out created); }
    internal static string ProcessInfo(uint processId, out long created)
    {
        IntPtr process = OpenProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            long exit, kernel, user;
            if (!GetProcessTimes(process, out created, out exit, out kernel, out user)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var path = new StringBuilder(512);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) {
                int error = Marshal.GetLastWin32Error();
                if (error != 122) throw new System.ComponentModel.Win32Exception(error);
                path.EnsureCapacity(32768); length = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            return path.ToString();
        } finally { CloseHandle(process); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(IntPtr process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int x, y; public uint mouseData, flags, time; public UIntPtr extra; }
    internal static INPUT Key(ushort vk, char scan, uint flags) { return new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { vk = vk, scan = scan, flags = flags } } }; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
