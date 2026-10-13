using System;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal sealed partial class MainForm : Form
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
    DelaySettings delays = new DelaySettings();
    readonly NumericUpDown[] delayInputs = new NumericUpDown[8];
    readonly ComboBox timingPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly Label presetDescription = new Label { Width = 420, Height = 32 };
    readonly CheckBox showAdvanced = new CheckBox { Text = "Advanced settings", AutoSize = true };
    bool displayingDelays;
    readonly Label pinState = new Label { AutoSize = true, Text = "No PIN entered" };
    readonly Label activity = new Label { AutoSize = true, Text = "PAUSED" };
    readonly CheckBox revealPin = new CheckBox { Text = "Show PIN", AutoSize = true };
    Button savePinButton;

    public MainForm(bool startInTray, bool testing = false)
    {
        SuspendLayout();
        try { BuildInterface(); }
        finally { ResumeLayout(true); }
        InitializeTray(testing);
        BindLifecycle(startInTray);
        BindSettings();
        LoadSettings(testing);
        if (!testing) StartWorker();
    }

    void InitializeTray(bool testing)
    {
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
    }

    void BindLifecycle(bool startInTray)
    {
        Resize += delegate { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        Shown += delegate { if (startInTray) Hide(); };
    }

    void BindSettings()
    {
        enabled.CheckedChanged += delegate {
            ConfigureWorker();
            if (enabled.Checked && !administrator) { enabled.Checked = false; return; }
            if (enabled.Checked && pin.Text.Length == 0) { enabled.Checked = false; SetStatus("Enter your PIN first."); return; }
            activity.Text = enabled.Checked ? "AUTOMATIC ENTRY ON" : "PAUSED";
            activity.ForeColor = enabled.Checked ? Color.FromArgb(21, 128, 61) : ForeColor;
            trayEnabled.Checked = enabled.Checked;
            tray.Text = enabled.Checked ? "AutoHello — Automatic entry enabled" : "AutoHello — Paused";
            SetStatus(enabled.Checked ? "Watching for a focused Windows Hello PIN field. Use tray > Exit to stop." : "Paused.");
            stickyStatus = false;
            SavePreferences();
        };
        submit.CheckedChanged += delegate { ConfigureWorker(); SavePreferences(); };
        pin.TextChanged += delegate {
            bool wasEnabled = enabled.Checked;
            enabled.Checked = false;
            savePinButton.Enabled = pin.TextLength > 0;
            pinState.Text = pin.TextLength > 0 ? "Session PIN - changes are not saved" : "No PIN entered";
            if (wasEnabled) SetStatus("Paused because the PIN changed. Enable automatic entry when ready.");
        };
    }

    void LoadSettings(bool testing)
    {
        string loadError = null;
        try {
            if (!testing && File.Exists(settings)) {
                pin.Text = StoredSettings.ReadPin(settings);
                pinState.Text = "Saved PIN loaded";
            }
        } catch { loadError = "Saved PIN could not be loaded. Enter and save it again."; }
        try {
            if (!testing && File.Exists(preferences)) {
                bool savedEnabled, savedSubmit;
                StoredSettings.ReadPreferences(preferences, out savedEnabled, out savedSubmit, out delays);
                DisplayDelays();
                lastSavedPreferences = StoredSettings.Preferences(savedEnabled, savedSubmit, delays);
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
    }

    void StartWorker()
    {
        // Create the UI handle before any worker report can be posted.
        IntPtr handle = Handle;
        worker = new AutomationWorker(new WindowsAutomation(), WorkerReport, true);
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

    void SavePreferences()
    {
        if (loading || !administrator) return;
        try {
            string value = StoredSettings.Preferences(enabled.Checked, submit.Checked, delays);
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
            AtomicFile.Write(settings, encrypted);
            pinState.Text = "PIN saved for this Windows account";
            SetStatus("PIN saved with Windows account encryption. Enable automatic entry when ready.");
        } catch { SetStatus("Could not save the PIN."); }
        finally { Array.Clear(plain, 0, plain.Length); }
    }

    void ConfigureWorker()
    {
        if (worker != null) workerGeneration = worker.Configure(administrator && enabled.Checked ? pin.Text : null, submit.Checked, delays);
    }

    void DisplayDelays()
    {
        int[] values = delays.Values;
        for (int i = 0; i < values.Length; i++) delayInputs[i].Value = values[i];
        displayingDelays = true;
        try { timingPreset.SelectedIndex = delays.PresetIndex; }
        finally { displayingDelays = false; }
        string[] descriptions = {
            "Wait 100 ms before typing.",
            "Wait 200 ms before typing.",
            "Wait 700 ms before typing.",
            "Custom timing. Adjust under Advanced settings."
        };
        presetDescription.Text = descriptions[timingPreset.SelectedIndex];
    }

    void ApplyDelays(DelaySettings value)
    {
        delays = value; DisplayDelays(); ConfigureWorker();
        SetStatus("Delays applied. Pending entry restarted; previous attempts remain blocked.");
        SavePreferences();
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
            settingHelp.Dispose();
            tray.Visible = false; tray.Dispose();
            if (trayMenu != null) trayMenu.Dispose();
            pin.Clear();
        }
        base.Dispose(disposing);
    }
}
