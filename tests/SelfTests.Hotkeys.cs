using System;
using System.IO;

internal static partial class SelfTests
{
    static void ToggleHotkeyTests()
    {
        int activeId = 0, removed = 0;
        uint modifiers = 0, virtualKey = 0;
        bool available = true;
        using (var hotkey = new ToggleHotkey(delegate(int id, uint flags, uint key) {
            if (!available) return false;
            activeId = id; modifiers = flags; virtualKey = key; return true;
        }, delegate(int id) { removed++; })) {
            Check(hotkey.Set(ToggleKey.ControlAltH), "register toggle shortcut");
            Check(modifiers == 0x4003 && virtualKey == 0x48, "toggle shortcut uses no-repeat and correct keys");
            IntPtr oldId = new IntPtr(activeId), detail = new IntPtr((virtualKey << 16) | (modifiers & 0xffffbfff));
            Check(hotkey.Matches(oldId, detail), "registered hotkey message matches");
            available = false;
            Check(!hotkey.Set(ToggleKey.F8) && hotkey.Key == ToggleKey.ControlAltH && removed == 0 && hotkey.Matches(oldId, detail), "collision preserves old shortcut");
            available = true;
            Check(hotkey.Set(ToggleKey.F8) && removed == 1 && virtualKey == 0x77, "replace shortcut releases old registration");
            Check(!hotkey.Matches(oldId, detail), "queued old shortcut ignored");
            Check(!hotkey.Matches(new IntPtr(activeId), detail), "wrong key data ignored");
            Check(hotkey.Set(ToggleKey.None) && removed == 2 && !hotkey.Matches(new IntPtr(activeId), new IntPtr(0x770000)), "None unregisters shortcut");
            hotkey.Set(ToggleKey.F10);
        }
        Check(removed == 3, "shutdown unregisters shortcut");
        string path = Path.Combine(Path.GetTempPath(), "AutoHello-toggle-" + Guid.NewGuid().ToString("N"));
        try {
            bool enabled, submit; DelaySettings delays; OverrideKey temporary; ToggleKey toggle;
            foreach (ToggleKey key in new[] { ToggleKey.None, ToggleKey.ControlAltH, ToggleKey.F8, new ToggleKey(15, 0x4a), new ToggleKey(8, 0x7a), new ToggleKey(0, 0x41) }) {
                foreach (OverrideKey skip in new[] { OverrideKey.None, OverrideKey.Shift }) {
                    File.WriteAllText(path, StoredSettings.Preferences(true, false, new DelaySettings(), skip, key));
                    StoredSettings.ReadPreferences(path, out enabled, out submit, out delays, out temporary, out toggle);
                    Check(enabled && !submit && toggle == key && temporary == skip, "toggle and override preferences coexist");
                }
            }
            File.WriteAllText(path, "enabled=false\nsubmit=true\noverride=Control\n");
            StoredSettings.ReadPreferences(path, out enabled, out submit, out delays, out temporary, out toggle);
            Check(toggle == ToggleKey.ControlAltH && temporary == OverrideKey.Control, "old preferences get default toggle shortcut");
            foreach (string invalid in new[] { "Unknown", "1", "99", "16,65", "3,17", "1,0", "0,256", "F8\ntoggle=F9" }) {
                File.WriteAllText(path, "enabled=false\nsubmit=true\ntoggle=" + invalid + "\n");
                bool rejected = false;
                try { StoredSettings.ReadPreferences(path, out enabled, out submit, out delays, out temporary, out toggle); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "invalid toggle preferences rejected");
            }
            File.WriteAllText(path, "enabled=false\nsubmit=true\ntoggle=ControlAltH\n");
            StoredSettings.ReadPreferences(path, out enabled, out submit, out delays, out temporary, out toggle);
            Check(toggle == ToggleKey.ControlAltH, "legacy preset shortcut preserved");
        } finally { File.Delete(path); }
        using (var field = new HotkeyField()) {
            field.CaptureShortcut(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.K, true);
            Check(field.Shortcut == new ToggleKey(14, 0x4b), "custom modifiers and key captured");
            field.CaptureShortcut(System.Windows.Forms.Keys.ControlKey, false);
            Check(field.Shortcut == new ToggleKey(14, 0x4b), "modifier alone does not replace shortcut");
            Check(!field.CaptureShortcut(System.Windows.Forms.Keys.Tab, false), "Tab keeps keyboard navigation");
            field.CaptureShortcut(System.Windows.Forms.Keys.F7, false);
            Check(field.Shortcut == new ToggleKey(0, 0x76), "custom single key captured");
        }
    }
    static void OverrideTests()
    {
        foreach (OverrideKey key in new[] { OverrideKey.Control, OverrideKey.Alt, OverrideKey.Windows, OverrideKey.Shift }) {
            var fake = new Fake { HeldOverride = key };
            using (var worker = new AutomationWorker(fake, delegate { }, false)) {
                worker.Configure("12", true, new DelaySettings { StableFocus = 0 }, key);
                worker.Step();
                Check(worker.State == DetectionState.Attempted && fake.Sends == 0, "selected modifier skips prompt");
                fake.HeldOverride = OverrideKey.None;
                worker.Step(); fake.Now += 1000; worker.Step();
                Check(fake.Sends == 0, "release does not unskip prompt");
                worker.Configure(null, false);
                worker.Configure("12", true, new DelaySettings { StableFocus = 0 }, OverrideKey.None);
                worker.Step(); worker.Step();
                Check(fake.Sends == 0, "skip survives pause and setting changes");
                fake.Target = NewTarget(2); worker.Step(); worker.Step();
                Check(fake.Sends == 1, "next prompt remains eligible");
            }
        }
        var other = new Fake { HeldOverride = OverrideKey.Alt };
        using (var worker = new AutomationWorker(other, delegate { }, false)) {
            worker.Configure("12", false, new DelaySettings { StableFocus = 0 }, OverrideKey.Control);
            worker.Step(); other.HeldOverride = OverrideKey.None; worker.Step(); worker.Step();
            Check(other.Sends == 1, "unselected modifier only postpones entry");
        }
        var late = new Fake();
        using (var worker = new AutomationWorker(late, delegate { }, false)) {
            worker.Configure("12", true, new DelaySettings { StableFocus = 0 }, OverrideKey.Control);
            worker.Step(); late.ForegroundHook = delegate { late.HeldOverride = OverrideKey.Control; };
            worker.Step(); late.ForegroundHook = null; late.HeldOverride = OverrideKey.None; worker.Step(); worker.Step();
            Check(late.Sends == 0 && worker.State == DetectionState.Attempted, "override at final validation skips");
        }
        for (int mode = 0; mode < 2; mode++) {
            var paced = new Fake();
            using (var worker = new AutomationWorker(paced, delegate { }, false)) {
                worker.Configure("12", true, new DelaySettings { StableFocus = 0, CharacterInterval = mode == 0 ? 100 : 0, EnterDelay = 100 }, OverrideKey.Shift);
                worker.Step(); worker.Step(); worker.Step();
                paced.HeldOverride = OverrideKey.Shift; paced.Now = 100; worker.Step();
                paced.HeldOverride = OverrideKey.None; paced.Now = 1000; worker.Step();
                Check(paced.Sends == 1 && worker.State == DetectionState.Attempted, "override cancels remaining characters or Enter without global pause");
                paced.Target = NewTarget(2); worker.Step(); worker.Step(); worker.Step();
                Check(paced.Sends == 2, "next prompt works after paced override");
            }
        }
        string path = Path.Combine(Path.GetTempPath(), "AutoHello-override-" + Guid.NewGuid().ToString("N"));
        try {
            bool enabled, submit; DelaySettings timing; OverrideKey restored;
            foreach (OverrideKey key in Enum.GetValues(typeof(OverrideKey))) {
                File.WriteAllText(path, StoredSettings.Preferences(true, false, new DelaySettings(), key));
                StoredSettings.ReadPreferences(path, out enabled, out submit, out timing, out restored);
                Check(restored == key && enabled && !submit, "override preference round trip");
            }
            File.WriteAllText(path, "enabled=true\nsubmit=false\n");
            StoredSettings.ReadPreferences(path, out enabled, out submit, out timing, out restored);
            Check(restored == OverrideKey.None, "legacy override disabled");
            foreach (string invalid in new[] { "Bogus", "99", "1", "Control\nextra=true" }) {
                File.WriteAllText(path, "enabled=true\nsubmit=false\noverride=" + invalid + "\n");
                bool rejected = false;
                try { StoredSettings.ReadPreferences(path, out enabled, out submit, out timing, out restored); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected, "invalid override rejected");
            }
        } finally { File.Delete(path); }
    }
}
