using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    void BindSettings()
    {
        enabled.CheckedChanged += delegate {
            if (loading) return;
            if (!enabled.Checked) SetRunning(false);
            MarkDirty();
        };
        submit.CheckedChanged += delegate { MarkDirty(); };
        overrideSelector.SelectedIndexChanged += delegate { MarkDirty(); };
        toggleSelector.TextChanged += delegate { MarkDirty(); };
        startup.CheckedChanged += delegate { MarkDirty(); };
        foreach (var input in delayInputs) input.ValueChanged += delegate {
            if (loading || displayingDelays) return;
            displayingDelays = true;
            try { timingPreset.SelectedIndex = 3; } finally { displayingDelays = false; }
            MarkDirty();
        };
        pin.TextChanged += delegate {
            if (loading) return;
            SetRunning(false);
            MarkDirty();
        };
    }

    void MarkDirty() { if (!loading) SetStatus("Unsaved changes"); }

    void LoadSettings(bool testing)
    {
        string loadError = null;
        string startupNotice = null;
        try {
            if (!testing && File.Exists(settings)) {
                pin.Text = StoredSettings.ReadPin(settings);
            }
        } catch { loadError = "Saved PIN could not be loaded. Enter and save it again."; }
        try {
            if (!testing && File.Exists(preferences)) {
                bool savedEnabled, savedSubmit;
                OverrideKey savedOverride;
                StoredSettings.ReadPreferences(preferences, out savedEnabled, out savedSubmit, out delays, out savedOverride, out toggleKey);
                overrideSelector.SelectedIndex = (int)savedOverride;
                toggleSelector.SetShortcut(toggleKey);
                DisplayDelays();
                submit.Checked = savedSubmit;
                enabled.Checked = administrator && pin.Text.Length > 0 && savedEnabled;
            }
        } catch { enabled.Checked = false; loadError = "Could not load preferences. Review your settings."; }
        try {
            if (!testing) {
                StartupState startupState = StartupTask.GetState();
                startup.Checked = startupState == StartupState.Enabled;
                if (startupState == StartupState.DifferentExecutable) startupNotice = "Startup points to another location. Check Start with Windows and Save to update it.";
                if (startupState == StartupState.DifferentConfiguration) startupNotice = "Unexpected startup configuration. Review it in Task Scheduler.";
            }
        }
        catch { startupNotice = "Could not read Windows startup settings."; }
        savedStartup = startup.Checked;
        activePin = pin.Text;
        activeSubmit = submit.Checked;
        activeOverride = (OverrideKey)overrideSelector.SelectedIndex;
        activeDelays = delays.Copy();
        SetRunning(enabled.Checked);
        loading = false;
        if (startupNotice != null) SetStatus(startupNotice);
        if (loadError != null) { enabled.Checked = false; SetStatus(loadError); }
        if (!administrator) SetStatus("Administrator permission is required. Click Restart as administrator.");
    }

    bool SaveAll()
    {
        if (!administrator) return false;
        ToggleKey previousShortcut = toggleKey;
        bool desiredEnabled = enabled.Checked;
        string stage = "settings";
        try {
            DelaySettings next = DelaySettings.FromValues(Array.ConvertAll(delayInputs, delegate(NumericUpDown input) { return Decimal.ToInt32(input.Value); }));
            if (desiredEnabled && pin.TextLength == 0) { SetStatus("Enter a PIN first."); return false; }
            stage = "hotkey";
            if (toggleHotkey != null && !toggleHotkey.Set(toggleSelector.Shortcut)) {
                SetStatus("Hotkey unavailable. Choose another."); return false;
            }
            // Pause before changing persisted credentials or startup behavior.
            SetRunning(false);
            stage = "PIN";
            if (pin.TextLength == 0) deleteFile(settings);
            else {
                byte[] plain = Encoding.UTF8.GetBytes(pin.Text);
                try { writeFile(settings, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            stage = "startup";
            if (startup.Checked != savedStartup) { setStartup(startup.Checked); savedStartup = startup.Checked; }
            stage = "preferences";
            string value = StoredSettings.Preferences(desiredEnabled, submit.Checked, next, (OverrideKey)overrideSelector.SelectedIndex, toggleSelector.Shortcut);
            writeFile(preferences, Encoding.UTF8.GetBytes(value));
            activePin = pin.Text; activeSubmit = submit.Checked;
            activeOverride = (OverrideKey)overrideSelector.SelectedIndex;
            activeDelays = next; delays = next.Copy(); toggleKey = toggleSelector.Shortcut;
            DisplayDelays(); SetRunning(desiredEnabled);
            SetStatus(running ? "Saved · On" : "Saved · Paused");
            return true;
        } catch {
            SetRunning(false);
            if (toggleHotkey != null && !toggleHotkey.Set(previousShortcut))
                SetStatus("Save failed. Previous hotkey unavailable; entry paused.");
            else SetStatus("Could not save " + stage + ". Some changes may be saved; entry paused.");
            // A failed save never resumes input with partially updated settings.
            return false;
        }
    }
    void DisplayDelays()
    {
        displayingDelays = true;
        int[] values = delays.Values;
        for (int i = 0; i < values.Length; i++) delayInputs[i].Value = values[i];
        try { timingPreset.SelectedIndex = delays.PresetIndex; }
        finally { displayingDelays = false; }
    }

    void StageDelays(DelaySettings value)
    {
        delays = value; DisplayDelays(); MarkDirty();
    }

}
