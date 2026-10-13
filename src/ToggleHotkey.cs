using System;

internal struct ToggleKey : IEquatable<ToggleKey>
{
    internal readonly uint Modifiers, VirtualKey;
    internal static readonly ToggleKey None = new ToggleKey();
    internal static readonly ToggleKey ControlAltH = new ToggleKey(3, 0x48), ControlShiftH = new ToggleKey(6, 0x48), AltShiftH = new ToggleKey(5, 0x48), F8 = new ToggleKey(0, 0x77), F9 = new ToggleKey(0, 0x78), F10 = new ToggleKey(0, 0x79);
    internal ToggleKey(uint modifiers, uint key) { Modifiers = modifiers; VirtualKey = key; Validate(); }
    internal void Validate()
    {
        if (Modifiers > 15 || (VirtualKey == 0 ? Modifiers != 0 : VirtualKey < 8 || VirtualKey > 254 || IsModifier(VirtualKey)))
            throw new System.IO.InvalidDataException("Invalid toggle shortcut.");
    }
    internal static bool IsModifier(uint key) { return key == 16 || key == 17 || key == 18 || key == 91 || key == 92 || (key >= 160 && key <= 165); }
    public bool Equals(ToggleKey other) { return Modifiers == other.Modifiers && VirtualKey == other.VirtualKey; }
    public override bool Equals(object other) { return other is ToggleKey && Equals((ToggleKey)other); }
    public override int GetHashCode() { return (int)(Modifiers * 397 ^ VirtualKey); }
    public static bool operator ==(ToggleKey left, ToggleKey right) { return left.Equals(right); }
    public static bool operator !=(ToggleKey left, ToggleKey right) { return !left.Equals(right); }
    internal string Serialize() { return Modifiers.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + VirtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    internal static bool TryParse(string value, out ToggleKey key)
    {
        key = None;
        switch (value) { // Preserve shortcuts saved by the preset-only version.
            case "None": return true;
            case "ControlAltH": key = ControlAltH; return true;
            case "ControlShiftH": key = ControlShiftH; return true;
            case "AltShiftH": key = AltShiftH; return true;
            case "F8": key = F8; return true;
            case "F9": key = F9; return true;
            case "F10": key = F10; return true;
        }
        string[] parts = value.Split(','); uint modifiers, vk;
        if (parts.Length != 2 || !UInt32.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out modifiers) ||
            !UInt32.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out vk)) return false;
        try { key = new ToggleKey(modifiers, vk); return true; }
        catch (System.IO.InvalidDataException) { return false; }
    }
    public override string ToString()
    {
        if (this == None) return "None";
        return ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
            ((Modifiers & 4) != 0 ? "Shift + " : "") + ((Modifiers & 8) != 0 ? "Windows + " : "") +
            new System.Windows.Forms.KeysConverter().ConvertToString((System.Windows.Forms.Keys)VirtualKey);
    }
}

internal sealed class HotkeyField : System.Windows.Forms.TextBox
{
    internal ToggleKey Shortcut { get; private set; }
    internal HotkeyField() { ReadOnly = true; ShortcutsEnabled = false; SetShortcut(ToggleKey.None); }
    internal void SetShortcut(ToggleKey shortcut) { Shortcut = shortcut; Text = shortcut.ToString(); }
    internal bool CaptureShortcut(System.Windows.Forms.Keys keys, bool windowsHeld)
    {
        uint vk = (uint)(keys & System.Windows.Forms.Keys.KeyCode);
        if (vk == 27) { SetShortcut(ToggleKey.None); return true; }
        if (ToggleKey.IsModifier(vk)) return true;
        uint modifiers = ((keys & System.Windows.Forms.Keys.Control) != 0 ? 2u : 0u) |
            ((keys & System.Windows.Forms.Keys.Alt) != 0 ? 1u : 0u) |
            ((keys & System.Windows.Forms.Keys.Shift) != 0 ? 4u : 0u) | (windowsHeld ? 8u : 0u);
        if (vk == 9 && (modifiers == 0 || modifiers == 4)) return false; // Tab navigation.
        SetShortcut(new ToggleKey(modifiers, vk));
        return true;
    }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message message, System.Windows.Forms.Keys keyData)
    {
        bool windowsHeld = (Native.GetKeyState(0x5b) & 0x8000) != 0 || (Native.GetKeyState(0x5c) & 0x8000) != 0;
        return CaptureShortcut(keyData, windowsHeld) || base.ProcessCmdKey(ref message, keyData);
    }
}

// Register the replacement before releasing the old shortcut so conflicts do
// not silently remove a working binding. Alternate IDs reject queued old events.
internal sealed class ToggleHotkey : IDisposable
{
    readonly Func<int, uint, uint, bool> register;
    readonly Action<int> unregister;
    int activeId;
    internal ToggleKey Key { get; private set; }
    internal ToggleHotkey(Func<int, uint, uint, bool> register, Action<int> unregister)
    {
        this.register = register; this.unregister = unregister;
    }
    internal bool Set(ToggleKey key)
    {
        key.Validate();
        if (key == Key) return true;
        int nextId = activeId == 0x4100 ? 0x4101 : 0x4100;
        if (key != ToggleKey.None && !register(nextId, key.Modifiers | 0x4000u, key.VirtualKey)) return false; // MOD_NOREPEAT
        if (activeId != 0) unregister(activeId);
        activeId = key == ToggleKey.None ? 0 : nextId;
        Key = key;
        return true;
    }
    internal bool Matches(IntPtr id, IntPtr detail)
    {
        long data = detail.ToInt64();
        return activeId != 0 && id.ToInt64() == activeId &&
            (data & 0xffff) == Key.Modifiers && ((data >> 16) & 0xffff) == Key.VirtualKey;
    }
    public void Dispose() { if (activeId != 0) unregister(activeId); activeId = 0; Key = ToggleKey.None; }
}
