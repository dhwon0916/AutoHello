using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

// All UIA objects stay on the single MTA worker. Snapshots contain identity only.
internal sealed class Target
{
    internal IntPtr Window;
    internal uint Process;
    internal long Created;
    internal string Field;
    internal string Dialog { get { return Window + ":" + Process + ":" + Created; } }
    internal bool Same(Target other) { return other != null && Dialog == other.Dialog && Field == other.Field; }
}

internal interface IAutomationIO
{
    long Milliseconds { get; }
    string Status { get; }
    bool ModifiersHeld { get; }
    Target Inspect();
    bool StillForeground(Target target);
    uint Send(Native.INPUT[] input);
}

internal interface IFocusEvents
{
    void Subscribe(Action changed);
    void Unsubscribe();
}

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
    readonly string expected = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "CredentialUIBroker.exe");
    public long Milliseconds { get { return clock.ElapsedMilliseconds; } }
    public string Status { get; private set; }
    public bool ModifiersHeld {
        get {
            // High bit is current down state. Never release or synthesize user keys.
            return (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x11) & 0x8000) != 0 ||
                (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
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
        Status = "Waiting for Windows Security to be the foreground window.";
        IntPtr window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        uint pid;
        Native.GetWindowThreadProcessId(window, out pid);
        long created;
        if (!String.Equals(Native.ProcessInfo(pid, out created), expected, StringComparison.OrdinalIgnoreCase)) return null;
        Status = "Windows Security detected. Focus its PIN box; keep it empty and do not type.";
        var focused = AutomationElement.FocusedElement;
        if (focused == null) return null;
        var info = focused.Current;
        if (info.ProcessId != pid || !info.IsPassword || !info.HasKeyboardFocus || !info.IsEnabled || info.ControlType != ControlType.Edit) return null;
        if (!MainForm.PinName(info.Name) && (info.LabeledBy == null || !MainForm.PinName(info.LabeledBy.Current.Name))) {
            Status = "Focused password field has no accessible PIN label. Entry skipped."; return null;
        }
        Status = "PIN field detected. Waiting for stable focus; emptiness is not verified.";
        // Never request ValuePattern.Value, TextPattern text, or password length.
        var target = new Target { Window = window, Process = pid, Created = created, Field = String.Join(".", focused.GetRuntimeId()) };
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

internal enum DetectionState { Paused, Watching, Stabilizing, Attempted }

internal sealed class AutomationWorker : IDisposable
{
    readonly object gate = new object();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly IAutomationIO io;
    readonly Action<int, string, bool, bool> report;
    readonly Thread thread;
    readonly HashSet<string> attempted = new HashSet<string>();
    char[] secret;
    bool submit, stopped;
    int generation;
    int cancellation;
    int focusEpoch;
    int candidateEpoch;
    int failures;
    long notBefore, lastInspection = -50;
    bool pending;
    DetectionState state;
    string eventWarning;
    Target candidate;
    long since;
    long lastObservation;
    int candidateGeneration;
    string lastStatus;
    int statusGeneration;
    int statusEpoch;
    internal bool IsLatestNotice(int version, string message)
    {
        lock (gate) return !stopped && version == generation && version == Thread.VolatileRead(ref cancellation) && message == lastStatus && statusEpoch == Thread.VolatileRead(ref focusEpoch);
    }
    internal DetectionState State { get { lock (gate) return state; } }
    // AutoResetEvent holds at most one wakeup; pending holds at most one inspection.
    internal void SignalFocusChange()
    {
        Interlocked.Increment(ref focusEpoch); // Invalidate even if Send's short lock is held.
        lock (gate) {
            if (stopped || secret == null) return;
            candidate = null; state = DetectionState.Watching; pending = true; wake.Set();
        }
    }
    internal int NextDelay {
        get { lock (gate) {
            if (stopped || secret == null) return Timeout.Infinite;
            long due = pending ? lastInspection + 50 : lastInspection + (state == DetectionState.Stabilizing ? 200 : 2000);
            return (int)Math.Max(0, Math.Min(Int32.MaxValue, Math.Max(due, notBefore) - io.Milliseconds));
        } }
    }
    internal AutomationWorker(IAutomationIO io, Action<int, string, bool, bool> report, bool start)
    {
        this.io = io; this.report = report;
        if (start) {
            thread = new Thread(Run) { IsBackground = true, Name = "AutoHello inspection" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }
    internal int Configure(string pin, bool enter)
    {
        // Publish cancellation before waiting for the short send/configuration lock.
        int requested = Interlocked.Increment(ref cancellation);
        lock (gate) {
            if (stopped) return generation;
            ClearSecret();
            secret = String.IsNullOrEmpty(pin) ? null : pin.ToCharArray();
            submit = enter;
            generation = requested;
            candidate = null; state = secret == null ? DetectionState.Paused : DetectionState.Watching;
            pending = secret != null; lastInspection = io.Milliseconds - 50;
            wake.Set();
            return requested;
        }
    }
    void ClearSecret() { if (secret != null) Array.Clear(secret, 0, secret.Length); secret = null; state = DetectionState.Paused; }
    void Run()
    {
        var events = io as IFocusEvents;
        try {
            if (events != null) {
                try { events.Subscribe(SignalFocusChange); }
                catch (Exception error) { eventWarning = " Focus events unavailable (" + error.GetType().Name + "); using fallback."; }
            }
            while (true) {
                lock (gate) { if (stopped) return; }
                int delay = NextDelay;
                if (delay == 0) Step();
                else wake.WaitOne(delay);
            }
        } finally {
            try { if (events != null) events.Unsubscribe(); }
            catch (Exception) { /* Shutdown cannot safely wait/retry a failed COM removal. */ }
            finally { wake.Dispose(); }
        }
    }
    bool Current(int version) { return !stopped && secret != null && generation == version && Thread.VolatileRead(ref cancellation) == version; }
    internal void Step()
    {
        int version, epoch;
        lock (gate) {
            if (stopped || secret == null || io.Milliseconds < notBefore) return;
            version = Thread.VolatileRead(ref generation); epoch = Thread.VolatileRead(ref focusEpoch);
            pending = false; lastInspection = io.Milliseconds;
        }
        try {
            long started = io.Milliseconds;
            Target target = io.Inspect();
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                failures = 0;
                bool modifiers = io.ModifiersHeld;
                string notice = target != null && attempted.Contains(target.Dialog)
                    ? "Already attempted this dialog identity. No retry, including after pause/resume. Complete the prompt manually."
                    : modifiers && target != null ? "Modifier key held. Entry postponed; release modifiers and leave the PIN field empty." : io.Status;
                notice += eventWarning;
                if (lastStatus != notice || statusGeneration != version || statusEpoch != epoch) {
                    bool changed = lastStatus != notice || statusGeneration != version;
                    lastStatus = notice; statusGeneration = version; statusEpoch = epoch;
                    // A previously queued identical message is current again; do not
                    // flood the UI for event bursts that leave detection unchanged.
                    if (changed)
                    report(version, lastStatus, false, false);
                }
                if (target == null || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
                if (attempted.Contains(target.Dialog)) { candidate = null; state = DetectionState.Attempted; return; }
                if (modifiers) { candidate = null; state = DetectionState.Watching; return; }
                if (candidateGeneration != version || candidateEpoch != epoch || !target.Same(candidate) || io.Milliseconds - lastObservation > 1000) {
                    state = DetectionState.Stabilizing;
                    candidate = target; candidateGeneration = version; candidateEpoch = epoch; since = lastObservation = io.Milliseconds; return;
                }
                lastObservation = io.Milliseconds;
                if (io.Milliseconds - since < 700) return;
            }
            started = io.Milliseconds;
            Target final = io.Inspect();
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                if (!target.Same(final) || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
            }
            // Process checks can block too: keep them outside the configuration lock.
            bool foreground = io.StillForeground(target);
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                if (!foreground || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
                if (io.ModifiersHeld) { candidate = null; state = DetectionState.Watching; return; }
                if (attempted.Count >= 4096) {
                    ClearSecret();
                    report(version, "Attempt history is full. Paused. Close all credential prompts before restarting AutoHello.", true, true);
                    return;
                }
                // Retain tombstones even after destruction: same-process HWND reuse is ambiguous.
                Native.INPUT[] batch = new Native.INPUT[secret.Length * 2 + (submit ? 2 : 0)];
                try {
                    for (int i = 0; i < secret.Length; i++) {
                        batch[i * 2] = Native.Key(0, secret[i], 4);
                        batch[i * 2 + 1] = Native.Key(0, secret[i], 6);
                    }
                    if (submit) { batch[batch.Length - 2] = Native.Key(13, '\0', 0); batch[batch.Length - 1] = Native.Key(13, '\0', 2); }
                    if (io.ModifiersHeld || !Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) { candidate = null; state = DetectionState.Watching; return; }
                    if (!attempted.Add(target.Dialog)) return;
                    candidate = null; state = DetectionState.Attempted;
                    // Configuration and shutdown serialize with this short native send, never with UIA.
                    uint sent = io.Send(batch);
                    if (sent != batch.Length) {
                        ClearSecret();
                        report(version, "Input blocked or partial (" + sent + "/" + batch.Length + "). Paused; inspect the prompt manually. No retry; Windows does not identify every blocking reason.", true, true);
                    } else report(version, "Input sent once. Authentication success is not verified. No retry for this dialog.", false, true);
                } finally { Array.Clear(batch, 0, batch.Length); }
            }
        } catch (Exception error) {
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                candidate = null;
                failures = Math.Min(failures + 1, 4);
                notBefore = io.Milliseconds + (1000 << (failures - 1));
                ClearSecret();
                report(version, "Inspection/input failed: " + error.GetType().Name + " (0x" + error.HResult.ToString("X8") + "). Paused. Review permissions before enabling again; inspection cooldown up to 8 seconds.", true, true);
            }
        }
    }
    public void Dispose()
    {
        Interlocked.Increment(ref cancellation);
        lock (gate) {
            if (stopped) return;
            stopped = true; ClearSecret(); wake.Set();
            if (thread == null) wake.Dispose();
        }
        // A provider call cannot be safely aborted. Do not join it on the UI thread;
        // the background worker discards its result and disposes its event on return.
    }
}

internal static class AtomicFile
{
    internal static void Write(string path, byte[] data)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new System.IO.FileStream(temporary, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None)) {
                stream.Write(data, 0, data.Length); stream.Flush(true);
            }
            if (System.IO.File.Exists(path)) System.IO.File.Replace(temporary, path, null);
            else System.IO.File.Move(temporary, path);
        } finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}

internal static class StoredSettings
{
    internal static byte[] ReadBounded(string path, int maximum)
    {
        using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read)) {
            if (stream.Length == 0 || stream.Length > maximum) throw new System.IO.InvalidDataException();
            using (var reader = new System.IO.BinaryReader(stream)) {
                int length = (int)stream.Length;
                byte[] data = reader.ReadBytes(length);
                if (data.Length != length) throw new System.IO.InvalidDataException();
                return data;
            }
        }
    }
    internal static string ReadPin(string path)
    {
        byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(ReadBounded(path, 65536), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try {
            string value = new System.Text.UTF8Encoding(false, true).GetString(plain);
            if (value.Length == 0 || value.Length > 127) throw new System.IO.InvalidDataException();
            return value;
        } finally { Array.Clear(plain, 0, plain.Length); }
    }
    internal static string Preferences(bool enabled, bool submit)
    {
        return "enabled=" + (enabled ? "true" : "false") + "\nsubmit=" + (submit ? "true" : "false") + "\n";
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit)
    {
        string value = new System.Text.UTF8Encoding(false, true).GetString(ReadBounded(path, 1024));
        using (var reader = new System.IO.StringReader(value.TrimStart('\uFEFF'))) {
            string first = reader.ReadLine(), second = reader.ReadLine();
            if ((first != "enabled=true" && first != "enabled=false") || (second != "submit=true" && second != "submit=false") || reader.ReadLine() != null)
                throw new System.IO.InvalidDataException();
            enabled = first == "enabled=true"; submit = second == "submit=true";
        }
    }
}
