using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    internal static bool VerifyTrayLifecycle()
    {
        using (var form = new MainForm(false, true, true)) {
            int writes = 0, startupChanges = 0, deletes = 0;
            string preferencesSaved = null;
            int notifications = 0;
            string notification = null;
            form.notifyToggle = delegate(string message) { notifications++; notification = message; };
            form.writeFile = delegate(string path, byte[] data) { writes++; if (path == form.preferences) preferencesSaved = System.Text.Encoding.UTF8.GetString(data); };
            form.deleteFile = delegate { deletes++; };
            form.setStartup = delegate { startupChanges++; };
            if (form.toggleSelector.Shortcut != ToggleKey.ControlAltH || !form.saveButton.Enabled) return false;
            if (!form.pin.UseSystemPasswordChar) return false;
            form.pin.Text = "test-only";
            form.ToggleAutomaticEntry();
            if (form.running) return false; // Unsaved PIN is never used.
            if (notifications != 1 || notification != "Automatic PIN entry is off. Save a PIN first.") return false;
            form.enabled.Checked = true;
            form.submit.Checked = false;
            form.startup.Checked = true;
            form.overrideSelector.SelectedIndex = 2;
            form.timingPreset.SelectedIndex = 0;
            form.toggleSelector.CaptureShortcut(Keys.Control | Keys.Alt | Keys.J, false);
            if (writes != 0 || startupChanges != 0 || form.running || form.activeDelays.StableFocus != 200 || form.toggleKey != ToggleKey.ControlAltH) return false;
            if (!form.SaveAll() || writes != 2 || startupChanges != 1 || !form.running || form.activeSubmit || form.activeOverride != OverrideKey.Alt || form.activeDelays.StableFocus != 100 || form.toggleKey != new ToggleKey(3, 0x4a)) return false;
            if (preferencesSaved == null || !preferencesSaved.Contains("toggle=3,74")) return false;
            form.toggleHotkey = new ToggleHotkey(delegate { return false; }, delegate { });
            form.toggleSelector.CaptureShortcut(Keys.Control | Keys.K, false);
            if (form.SaveAll() || writes != 2 || form.toggleKey != new ToggleKey(3, 0x4a)) return false;
            form.toggleHotkey.Dispose(); form.toggleHotkey = null;
            form.toggleSelector.SetShortcut(form.toggleKey);
            form.ToggleAutomaticEntry();
            if (form.running || form.trayEnabled.Checked || writes != 2) return false;
            if (notifications != 2 || notification != "Automatic PIN entry is off.") return false;
            form.trayEnabled.PerformClick();
            if (!form.running || !form.trayEnabled.Checked || writes != 2) return false;
            if (notifications != 3 || notification != "Automatic PIN entry is on.") return false;
            form.toggleSelector.CaptureShortcut(Keys.Escape, false);
            if (form.toggleSelector.Shortcut != ToggleKey.None || form.toggleKey == ToggleKey.None) return false;
            if (!form.SaveAll() || form.toggleKey != ToggleKey.None || !preferencesSaved.Contains("toggle=0,0")) return false;
            form.revealPin.Checked = true;
            if (form.pin.UseSystemPasswordChar) return false;
            form.OnDeactivate(EventArgs.Empty);
            if (!form.pin.UseSystemPasswordChar || form.revealPin.Checked) return false;
            Size size = form.ClientSize;
            form.showAdvanced.Checked = true;
            if (form.ClientSize != size) return false;
            form.delayInputs[0].Value = 321;
            if (form.activeDelays.StableFocus != 100 || form.timingPreset.SelectedIndex != 3) return false;
            if (!form.SaveAll() || form.activeDelays.StableFocus != 321) return false;
            form.pin.Clear();
            if (form.running || deletes != 0) return false;
            if (!form.SaveAll() || deletes != 1 || form.activePin.Length != 0) return false;
            form.pin.Text = "new-test-only";
            form.enabled.Checked = true;
            form.writeFile = delegate { throw new System.IO.IOException(); };
            if (form.SaveAll() || form.running || !form.status.Text.Contains("Could not save")) return false;
            var close = new FormClosingEventArgs(CloseReason.UserClosing, false);
            form.OnFormClosing(close);
            if (!close.Cancel) return false;
            form.exiting = true;
            close = new FormClosingEventArgs(CloseReason.UserClosing, false);
            form.OnFormClosing(close);
            if (close.Cancel) return false;
        }
        using (var form = new MainForm(false, true, false)) {
            if (form.saveButton.Enabled || form.enabled.Enabled || form.trayEnabled.Enabled || form.SaveAll()) return false;
        }
        return true;
    }
}
