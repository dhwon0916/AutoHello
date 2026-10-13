using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    internal static bool VerifyTrayLifecycle()
    {
        using (var form = new MainForm(false, true)) {
            if (!form.pin.UseSystemPasswordChar || form.savePinButton.Enabled) return false;
            form.pin.Text = "test-only";
            if (!form.savePinButton.Enabled || !form.pinState.Text.Contains("not saved")) return false;
            form.revealPin.Checked = true;
            if (form.pin.UseSystemPasswordChar) return false;
            form.OnDeactivate(EventArgs.Empty);
            if (!form.pin.UseSystemPasswordChar || form.revealPin.Checked) return false;
            form.pin.Clear();
            if (form.savePinButton.Enabled) return false;
            Size originalSize = form.ClientSize;
            form.showAdvanced.Checked = true;
            if (form.ClientSize != originalSize) return false;
            form.showAdvanced.Checked = false;
            form.ApplyDelays(new DelaySettings { StableFocus = 0, IdleInterval = 5000 });
            if (form.delayInputs[0].Value != 0 || form.delayInputs[1].Value != 5000) return false;
            form.ApplyDelays(new DelaySettings());
            if (form.delayInputs[0].Value != 200 || form.delayInputs[1].Value != 2000) return false;
            if (form.timingPreset.SelectedIndex != 1 || form.showAdvanced.Checked) return false;
            form.timingPreset.SelectedIndex = 0;
            if (form.delays.StableFocus != 100 || form.delayInputs[0].Value != 100) return false;
            form.timingPreset.SelectedIndex = 2;
            if (form.delays.StableFocus != 700) return false;
            form.timingPreset.SelectedIndex = 3;
            if (!form.showAdvanced.Checked || form.delays.StableFocus != 700) return false;
            form.ApplyDelays(new DelaySettings { StableFocus = 123 });
            if (form.timingPreset.SelectedIndex != 3) return false;
            form.ApplyDelays(new DelaySettings());
            if (form.timingPreset.SelectedIndex != 1) return false;
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

}
