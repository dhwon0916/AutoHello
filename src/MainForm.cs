using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Windows.Forms;

internal sealed partial class MainForm : Form
{
    readonly TextBox pin = new TextBox { UseSystemPasswordChar = true, Width = 330, MaxLength = 127 };
    readonly CheckBox enabled = new CheckBox { Text = "Automatic PIN entry", AutoSize = true };
    readonly CheckBox submit = new CheckBox { Text = "Press Enter after PIN", Checked = true, AutoSize = true };
    readonly Label status = new Label { AutoSize = false, Width = 420, Height = 65, Text = "Paused. Set your PIN, then enable automatic entry." };
    AutomationWorker worker;
    WindowsAutomation automationIO;
    ToggleHotkey toggleHotkey;
    bool hotkeysReady;
    ToggleKey toggleKey = ToggleKey.ControlAltH;
    ForegroundEvents foregroundEvents;
    int workerGeneration;
    bool disposed;
    bool stickyStatus;
    ContextMenuStrip trayMenu;
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoHello", "pin.bin");
    readonly bool administrator = Program.IsAdministrator();
    readonly CheckBox startup = new CheckBox { Text = "Start with Windows", AutoSize = true };
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ToolStripMenuItem trayEnabled = new ToolStripMenuItem("Automatic PIN entry");
    readonly string preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoHello", "preferences.txt");
    bool loading = true;
    bool exiting;
    DelaySettings delays = new DelaySettings();
    readonly NumericUpDown[] delayInputs = new NumericUpDown[8];
    readonly ComboBox timingPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox overrideSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Temporary override hotkey" };
    readonly HotkeyField toggleSelector = new HotkeyField { Dock = DockStyle.Top, AccessibleName = "Toggle automatic entry hotkey" };
    readonly CheckBox showAdvanced = new CheckBox { Text = "Advanced settings", AutoSize = true };
    bool displayingDelays;
    readonly CheckBox revealPin = new CheckBox { Text = "Show PIN", AutoSize = true };
    Button saveButton;
    string activePin;
    bool running, activeSubmit = true, savedStartup;
    OverrideKey activeOverride;
    DelaySettings activeDelays = new DelaySettings();
    Action<string, byte[]> writeFile = AtomicFile.Write;
    Action<string> deleteFile = File.Delete;
    Action<bool> setStartup = StartupTask.SetEnabled;
    Action<string> notifyToggle;

    public MainForm(bool startInTray, bool testing = false, bool? testAdministrator = null)
    {
        if (testing && testAdministrator.HasValue) administrator = testAdministrator.Value;
        SuspendLayout();
        try { BuildInterface(); }
        finally { ResumeLayout(true); }
        InitializeTray(testing);
        BindLifecycle(startInTray);
        BindSettings();
        LoadSettings(testing);
        hotkeysReady = !testing;
        if (!testing) StartWorker();
    }

    void InitializeTray(bool testing)
    {
        var menu = new ContextMenuStrip();
        trayMenu = menu;
        menu.Items.Add("Open settings", null, delegate { ShowSettings(); });
        trayEnabled.Enabled = administrator;
        trayEnabled.Click += delegate { ToggleAutomaticEntry(); };
        menu.Items.Add(trayEnabled);
        var trayAdmin = menu.Items.Add("Run as administrator", null, delegate { RestartAsAdministrator(); });
        trayAdmin.Enabled = !administrator;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
        tray.Icon = SystemIcons.Shield;
        tray.ContextMenuStrip = menu;
        tray.Text = "AutoHello — Paused";
        tray.Visible = !testing;
        notifyToggle = delegate(string message) {
            if (!testing && !disposed && tray.Visible)
                tray.ShowBalloonTip(3000, "AutoHello", message, ToolTipIcon.Info);
        };
        tray.DoubleClick += delegate { ShowSettings(); };
    }

    void BindLifecycle(bool startInTray)
    {
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        Shown += delegate { if (startInTray) Hide(); };
    }

    void SetRunning(bool value)
    {
        running = administrator && value && !String.IsNullOrEmpty(activePin);
        bool previous = loading; loading = true;
        try { enabled.Checked = running; } finally { loading = previous; }
        trayEnabled.Checked = running;
        tray.Text = running ? "AutoHello — On" : "AutoHello — Paused";
        ConfigureWorker();
        SetStatus(running ? "On" : "Paused");
        stickyStatus = false;
    }
    void StartWorker()
    {
        // Create the UI handle before any worker report can be posted.
        IntPtr handle = Handle;
        InitializeToggleHotkey();
        automationIO = new WindowsAutomation();
        worker = new AutomationWorker(automationIO, WorkerReport, true);
        try { foregroundEvents = new ForegroundEvents(worker.SignalFocusChange); }
        catch (Exception error) { SetStatus("Foreground notifications unavailable (" + error.GetType().Name + "). Detection will use focus events and fallback checks."); }
        ConfigureWorker();
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

    void ConfigureWorker()
    {
        if (worker != null) workerGeneration = worker.Configure(administrator && running ? activePin : null, activeSubmit, activeDelays, activeOverride);
    }

    void SetStatus(string message) { stickyStatus = true; status.Text = message; settingHelp.SetToolTip(status, message); }

    void InitializeToggleHotkey()
    {
        if (!hotkeysReady || !administrator || disposed || toggleHotkey != null) return;
        IntPtr window = Handle;
        toggleHotkey = new ToggleHotkey(
            delegate(int id, uint modifiers, uint key) { return Native.RegisterHotKey(window, id, modifiers, key); },
            delegate(int id) { Native.UnregisterHotKey(window, id); });
        if (!toggleHotkey.Set(toggleKey)) SetStatus("Toggle hotkey unavailable. Choose another.");
    }

    void ToggleAutomaticEntry()
    {
        if (disposed || !administrator) return;
        // Do not interpret the enabling shortcut's own modifiers as a request
        // to skip the currently focused prompt. Normal modifier safety still applies.
        if (!running && automationIO != null) automationIO.IgnoreOverrideUntilRelease();
        bool enabling = !running;
        SetRunning(!running);
        string message = running ? "Automatic PIN entry is on." :
            enabling ? "Automatic PIN entry is off. Save a PIN first." : "Automatic PIN entry is off.";
        if (enabling && !running) SetStatus(message);
        notifyToggle(message);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InitializeToggleHotkey();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (toggleHotkey != null) { toggleHotkey.Dispose(); toggleHotkey = null; }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && toggleHotkey != null && toggleHotkey.Matches(message.WParam, message.LParam)) {
            if (toggleSelector.Focused) toggleSelector.SetShortcut(toggleHotkey.Key);
            else ToggleAutomaticEntry();
            return;
        }
        base.WndProc(ref message);
    }

    void WorkerReport(int version, string message, bool pause, bool durable)
    {
        try {
            BeginInvoke((Action)delegate {
                if (disposed || version != workerGeneration) return;
                if (!durable && !worker.IsLatestNotice(version, message)) return;
                if (pause) SetRunning(false);
                if (durable || !stickyStatus) { SetStatus(message); stickyStatus = durable; }
            });
        } catch (InvalidOperationException) { /* Form handle is closing. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) {
            disposed = true; loading = true;
            if (toggleHotkey != null) { toggleHotkey.Dispose(); toggleHotkey = null; }
            if (worker != null) worker.Dispose();
            if (foregroundEvents != null) foregroundEvents.Dispose();
            settingHelp.Dispose();
            tray.Visible = false; tray.Dispose();
            if (trayMenu != null) trayMenu.Dispose();
            pin.Clear();
            activePin = null;
        }
        base.Dispose(disposing);
    }
}
