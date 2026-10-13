using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

internal sealed partial class MainForm
{
    readonly ToolTip settingHelp = new ToolTip {
        InitialDelay = 450, ReshowDelay = 100, AutoPopDelay = 20000, ShowAlways = true
    };

    void Explain(Control control, string explanation)
    {
        settingHelp.SetToolTip(control, explanation);
        control.AccessibleDescription = explanation;
        control.HelpRequested += delegate(object sender, HelpEventArgs e) {
            MessageBox.Show(this, explanation, "AutoHello help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            e.Handled = true;
        };
    }

    void BuildInterface()
    {
        Text = administrator ? "AutoHello — Administrator" : "AutoHello — Administrator required";
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F);
        BackColor = SystemColors.Control;
        ForeColor = SystemColors.ControlText;
        ClientSize = new Size(460, 540);
        MinimumSize = new Size(440, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Shield;
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var cards = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 8, 0) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(cards);
        shell.Controls.Add(scroll, 0, 0);
        var layout = Section(cards, "Windows Hello PIN");
        pin.Dock = DockStyle.Top;
        pin.AccessibleName = "Windows Hello PIN";
        layout.Controls.Add(pin);
        layout.Controls.Add(revealPin);
        revealPin.CheckedChanged += delegate { pin.UseSystemPasswordChar = !revealPin.Checked; };
        Deactivate += delegate { revealPin.Checked = false; };
        VisibleChanged += delegate { if (!Visible) revealPin.Checked = false; };
        var pinActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        savePinButton = ActionButton("&Save PIN");
        savePinButton.Enabled = false;
        savePinButton.Click += delegate { SavePin(); };
        pinActions.Controls.Add(savePinButton);
        layout.Controls.Add(pinActions);
        layout.Controls.Add(pinState);
        layout = Section(cards, "Automatic entry");
        layout.Controls.Add(enabled);
        layout.Controls.Add(Copy("Keep the Hello PIN field empty. Do not type while automatic entry is enabled."));
        layout.Controls.Add(submit);
        layout.Controls.Add(startup);
        var restart = ActionButton("Restart as &administrator");
        restart.Visible = !administrator;
        restart.Click += delegate { RestartAsAdministrator(); };

        layout = Section(cards, "Entry speed");
        timingPreset.Items.AddRange(new object[] { "Fast", "Balanced (default)", "Conservative", "Custom" });
        timingPreset.Enabled = administrator;
        timingPreset.Dock = DockStyle.Top;
        timingPreset.AccessibleName = "Entry speed preset";
        layout.Controls.Add(timingPreset);
        presetDescription.AutoSize = true;
        presetDescription.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(presetDescription);
        layout.Controls.Add(showAdvanced);
        showAdvanced.Text = "Show advanced timing settings";
        var advanced = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Visible = false, Margin = new Padding(0, 12, 0, 0) };
        advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(advanced);
        showAdvanced.CheckedChanged += delegate {
            advanced.Visible = showAdvanced.Checked;

        };
        timingPreset.SelectedIndexChanged += delegate {
            if (displayingDelays) return;
            if (timingPreset.SelectedIndex == 3) {
                showAdvanced.Checked = true;
                presetDescription.Text = "Edit individual delays below, then choose Apply delays.";
            } else ApplyDelays(DelaySettings.Preset(timingPreset.SelectedIndex));
        };
        advanced.Controls.Add(new Label { Text = "Delays (milliseconds)", AutoSize = true });
        string[] delayLabels = { "Stable focus before entry", "Idle fallback interval", "Stable-focus check interval", "Prompt readiness check interval", "Prompt readiness window", "Event burst spacing", "Time between characters", "Wait before Enter" };
        string[] delayHelp = {
            "Wait before typing into the focused PIN box.",
            "How often to check for a prompt while idle.",
            "How often to check that the PIN box stays focused.",
            "How often to check a prompt that is opening.",
            "How long to keep checking a newly opened prompt.",
            "Minimum wait between checks triggered by focus changes.",
            "Pause between characters. Lower values type faster; 0 types all at once.",
            "Pause after typing before pressing Enter, if enabled."
        };
        for (int i = 0; i < delayInputs.Length; i++) {
            var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 3, 0, 3) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            row.Controls.Add(new Label { Text = delayLabels[i], AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 4) });
            delayInputs[i] = new NumericUpDown { Dock = DockStyle.Top, AccessibleName = delayLabels[i] + " in milliseconds", Minimum = (i == 1 || i == 2 || i == 3) ? 1 : 0, Maximum = Int32.MaxValue, ThousandsSeparator = true, Enabled = administrator };
            Explain(delayInputs[i], delayHelp[i]);
            Explain(row.Controls[0], delayHelp[i]);
            row.Controls.Add(delayInputs[i]); advanced.Controls.Add(row);
        }
        DisplayDelays();
        var delayButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
        var applyDelays = ActionButton("&Apply delays");
        var resetDelays = ActionButton("Reset to &Balanced");
        applyDelays.Enabled = resetDelays.Enabled = administrator;
        applyDelays.Click += delegate {
            int[] values = Array.ConvertAll(delayInputs, delegate(NumericUpDown input) { return Decimal.ToInt32(input.Value); });
            ApplyDelays(DelaySettings.FromValues(values));
        };
        resetDelays.Click += delegate { ApplyDelays(new DelaySettings()); };
        delayButtons.Controls.Add(applyDelays); delayButtons.Controls.Add(resetDelays); advanced.Controls.Add(delayButtons);
        advanced.Controls.Add(Copy("Click Apply delays to save your changes."));
        var footer = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 8, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.AutoSize = true;
        status.Margin = new Padding(0, 6, 0, 8);
        footer.Controls.Add(status);
        var footerActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
        var hide = ActionButton("Hide to &tray");
        hide.Click += delegate { Hide(); };
        var exit = ActionButton("E&xit AutoHello");
        exit.Click += delegate { exiting = true; Close(); };
        footerActions.Controls.Add(restart); footerActions.Controls.Add(hide); footerActions.Controls.Add(exit);
        footer.Controls.Add(footerActions);
        footer.Controls.Add(Copy("Closing this window hides it to the tray."));
        shell.Controls.Add(footer, 0, 1);
        var forget = ActionButton("&Forget saved PIN");
        forget.Click += delegate {
            enabled.Checked = false;
            try { if (File.Exists(settings)) File.Delete(settings); pin.Clear(); pinState.Text = "No PIN saved"; SetStatus("Saved PIN removed."); }
            catch { SetStatus("Could not remove the saved PIN file."); }
        };
        pinActions.Controls.Add(forget);
        Explain(pin, "The Windows Hello PIN to enter automatically.");
        Explain(revealPin, "Show the PIN until this window loses focus.");
        Explain(savePinButton, "Remember this PIN for your Windows account.");
        Explain(forget, "Delete the saved PIN and pause automatic entry.");
        Explain(pinState, "Whether the current PIN is saved.");
        Explain(enabled, "Enter your PIN automatically once per prompt.");
        Explain(submit, "Press Enter automatically after typing the PIN.");
        Explain(startup, "Run AutoHello in the tray when you sign in.");
        Explain(timingPreset, "Choose a speed preset; changes save immediately.");
        Explain(presetDescription, settingHelp.GetToolTip(timingPreset));
        Explain(showAdvanced, "Adjust individual delays in milliseconds.");
        Explain(applyDelays, "Save and use these delays.");
        Explain(resetDelays, "Restore all delays to Balanced defaults.");
        Explain(restart, "Restart as administrator. Save your PIN first.");
        Explain(hide, "Keep AutoHello running in the tray.");
        Explain(exit, "Stop AutoHello and close it.");
        Controls.Add(shell);
        cards.SizeChanged += delegate { WrapLabels(cards); };
        footer.SizeChanged += delegate { WrapLabels(footer); };
        enabled.Enabled = administrator;
        submit.Enabled = administrator;
        startup.Enabled = administrator;
    }

    static Label Copy(string text)
    {
        return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 5), ForeColor = SystemColors.ControlText };
    }

    TableLayoutPanel Section(TableLayoutPanel parent, string title)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = Padding.Empty, Margin = new Padding(0, 0, 0, 10) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        card.SizeChanged += delegate { WrapLabels(card); };
        parent.Controls.Add(card);
        return card;
    }

    static void WrapLabels(Control parent)
    {
        foreach (Control child in parent.Controls) {
            Label label = child as Label;
            if (label != null && label.AutoSize) {
                int width = parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal;
                var table = parent as TableLayoutPanel;
                if (table != null && table.ColumnCount == 2) width -= (int)table.ColumnStyles[1].Width;
                Size maximum = new Size(Math.Max(100, width), 0);
                if (label.MaximumSize != maximum) label.MaximumSize = maximum;
            }
            else if (child.HasChildren) WrapLabels(child);
        }
    }

    static Button ActionButton(string text)
    {
        return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 2, 4, 2), Margin = new Padding(0, 0, 6, 4), UseVisualStyleBackColor = true };
    }
}
