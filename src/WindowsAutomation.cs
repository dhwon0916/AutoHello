using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

// Created/disposed on the WinForms thread, whose existing message loop delivers
// out-of-context WinEvents. Never inspect accessibility data in this callback.
internal sealed class ForegroundEvents : IDisposable
{
    delegate void WinEvent(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time);
    readonly WinEvent callback;
    GCHandle root;
    IntPtr hook;
    internal ForegroundEvents(Action changed)
    {
        callback = delegate { changed(); };
        root = GCHandle.Alloc(callback);
        hook = SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, OUTOFCONTEXT; include our own windows.
        if (hook == IntPtr.Zero) { root.Free(); throw new System.ComponentModel.Win32Exception(); }
    }
    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        // Must be called on the registering thread.
        if (!UnhookWinEvent(hook)) return; // Keep delegate alive if Windows still owns it.
        hook = IntPtr.Zero;
        root.Free();
    }
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
}

internal sealed class WindowsAutomation : IAutomationIO, IFocusEvents
{
    AutomationFocusChangedEventHandler focusChanged;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly CacheRequest inspectionCache = CreateInspectionCache();
    static CacheRequest CreateInspectionCache()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element };
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.HasKeyboardFocusProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.LabeledByProperty);
        request.Add(AutomationElement.RuntimeIdProperty);
        return request;
    }
    readonly string expected = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "CredentialUIBroker.exe");
    public long Milliseconds { get { return clock.ElapsedMilliseconds; } }
    public string Status { get; private set; }
    public bool WatchReadiness { get; private set; }
    volatile bool ignoreOverride;
    internal void IgnoreOverrideUntilRelease() { ignoreOverride = true; }
    public bool OverrideHeld(OverrideKey key)
    {
        if (ignoreOverride) {
            if (!ModifiersHeld) ignoreOverride = false;
            return false;
        }
        switch (key) {
            case OverrideKey.Control: return (GetAsyncKeyState(0x11) & 0x8000) != 0;
            case OverrideKey.Alt: return (GetAsyncKeyState(0x12) & 0x8000) != 0;
            case OverrideKey.Windows: return (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            case OverrideKey.Shift: return (GetAsyncKeyState(0x10) & 0x8000) != 0;
            default: return false;
        }
    }
    public bool ModifiersHeld {
        get {
            // High bit is current down state. Never release or synthesize user keys.
            bool held = (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            if (!held) ignoreOverride = false;
            return held;
        }
    }
    public void Subscribe(Action changed)
    {
        focusChanged = delegate { changed(); }; // Do not dereference the event's AutomationElement.
        Automation.AddAutomationFocusChangedEventHandler(focusChanged);
    }
    public void Unsubscribe()
    {
        if (focusChanged != null) Automation.RemoveAutomationFocusChangedEventHandler(focusChanged);
        focusChanged = null;
    }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    public Target Inspect()
    {
        WatchReadiness = false;
        Status = "Waiting for Windows Security to be the foreground window.";
        IntPtr window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        uint pid;
        Native.GetWindowThreadProcessId(window, out pid);
        long created;
        if (!String.Equals(Native.ProcessInfo(pid, out created), expected, StringComparison.OrdinalIgnoreCase)) return null;
        WatchReadiness = true;
        Status = "Windows Security detected. Focus its PIN box; keep it empty and do not type.";
        // Fetch only eligibility metadata in one request, refreshed on every
        // inspection (including final validation). Never cache protected values.
        AutomationElement focused;
        using (inspectionCache.Activate()) focused = AutomationElement.FocusedElement;
        if (focused == null) return null;
        var info = focused.Cached;
        if (info.ProcessId != pid || !info.IsPassword || !info.HasKeyboardFocus || !info.IsEnabled || info.ControlType != ControlType.Edit) return null;
        if (!PinLabel.Matches(info.Name) && (info.LabeledBy == null || !PinLabel.Matches(info.LabeledBy.Current.Name))) {
            Status = "Focused password field has no accessible PIN label. Entry skipped."; return null;
        }
        Status = "PIN field detected. Waiting for stable focus; emptiness is not verified.";
        // Never request ValuePattern.Value, TextPattern text, or password length.
        var target = new Target { Window = window, Process = pid, Created = created,
            Field = (int[])focused.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) };
        return StillForeground(target) ? target : null;
    }
    public bool StillForeground(Target target)
    {
        if (Native.GetForegroundWindow() != target.Window) return false;
        uint pid;
        Native.GetWindowThreadProcessId(target.Window, out pid);
        if (pid != target.Process) return false;
        long created;
        return String.Equals(Native.ProcessInfo(pid, out created), expected, StringComparison.OrdinalIgnoreCase) && created == target.Created && Native.GetForegroundWindow() == target.Window;
    }
    public uint Send(Native.INPUT[] input) { return Native.SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(Native.INPUT))); }
}

internal static class PinLabel
{
    static readonly System.Text.RegularExpressions.Regex pattern = new System.Text.RegularExpressions.Regex(@"\bPIN\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    internal static bool Matches(string name) { return name != null && pattern.IsMatch(name); }
}
