using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Threading;

internal static partial class SelfTests
{
    internal sealed class Fake : IAutomationIO, IFocusEvents
    {
        internal long Now;
        internal Target Target = NewTarget(1);
        internal bool Foreground = true, ThrowSend, Held;
        internal bool Readiness = true;
        internal OverrideKey HeldOverride;
        internal char FirstScan;
        internal int Inspections, Sends;
        internal uint? Sent;
        internal Action InspectHook, ForegroundHook, SendHook;
        internal Action FocusEvent, OnUnsubscribe;
        int eventThread;
        internal Native.INPUT[] Batch;
        public long Milliseconds { get { return Now; } }
        public string Status { get { return "fake status"; } }
        public bool WatchReadiness { get { return Readiness; } }
        public bool ModifiersHeld { get { return Held || HeldOverride != OverrideKey.None; } }
        public bool OverrideHeld(OverrideKey key) { return key != OverrideKey.None && HeldOverride == key; }
        public void Subscribe(Action changed) { eventThread = Thread.CurrentThread.ManagedThreadId; FocusEvent = changed; }
        public void Unsubscribe() {
            Check(eventThread == Thread.CurrentThread.ManagedThreadId, "unsubscribe on subscribing thread");
            FocusEvent = null; if (OnUnsubscribe != null) OnUnsubscribe();
        }
        public Target Inspect() { Inspections++; if (InspectHook != null) InspectHook(); return Target; }
        public bool StillForeground(Target target) { if (ForegroundHook != null) ForegroundHook(); return Foreground; }
        public uint Send(Native.INPUT[] input) { Sends++; Batch = input; FirstScan = (char)input[0].data.keyboard.scan; if (SendHook != null) SendHook(); if (ThrowSend) throw new InvalidOperationException(); return Sent ?? (uint)input.Length; }
    }
    static void IdentityTests()
    {
        var original = NewTarget(10);
        var same = NewTarget(10);
        Check(original.Same(same), "matching target identity");
        same.Field = new[] { 1, 42, 0 };
        Check(!original.Same(same), "runtime ID length distinguishes fields");
        same.Field = null;
        Check(!original.Same(same), "missing runtime ID fails closed");
        same.Field = new int[0];
        Check(!same.Same(same), "empty runtime ID fails closed");
        same = NewTarget(10);
        var records = new System.Collections.Generic.HashSet<DialogIdentity>();
        records.Add(original.Dialog);
        Check(records.Contains(same.Dialog), "equal identities share hash semantics");
        same.Field = new[] { 1, 43 };
        Check(!original.Same(same) && records.Contains(same.Dialog), "new field does not permit dialog retry");
        Check(!records.Contains(NewTarget(11).Dialog), "process lifetime distinguishes dialog");
        same = NewTarget(10); same.Process++;
        Check(!records.Contains(same.Dialog), "process ID distinguishes dialog");
        same = NewTarget(10); same.Window = new IntPtr(43);
        Check(!records.Contains(same.Dialog), "window handle distinguishes dialog");
        Check(PinLabel.Matches("Enter your PIN") && PinLabel.Matches("pin") &&
            !PinLabel.Matches("spindle") && !PinLabel.Matches("PIN_code") && !PinLabel.Matches(null), "PIN label boundaries");
        const int count = 100000;
        var legacy = new System.Collections.Generic.HashSet<string>();
        legacy.Add(original.Window + ":" + original.Process + ":" + original.Created);
        records.Contains(original.Dialog); // Warm both lookup paths before measuring.
        int matches = 0;
        int collections = GC.CollectionCount(0);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
            if (legacy.Contains(original.Window + ":" + original.Process + ":" + original.Created)) matches++;
        watch.Stop();
        Console.WriteLine("String identity: " + count + " lookups, " + watch.ElapsedMilliseconds + " ms, " + (GC.CollectionCount(0) - collections) + " Gen0 collections");
        collections = GC.CollectionCount(0); watch.Restart();
        for (int i = 0; i < count; i++) if (records.Contains(original.Dialog)) matches++;
        watch.Stop();
        Console.WriteLine("Value identity: " + count + " lookups, " + watch.ElapsedMilliseconds + " ms, " + (GC.CollectionCount(0) - collections) + " Gen0 collections");
        Check(matches == count * 2, "identity benchmark lookup results");
    }

    static Target NewTarget(long created) { return new Target { Window = new IntPtr(42), Process = 77, Created = created, Field = new[] { 1, 42 } }; }
    static void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Self-test: " + name); }
    static void Arm(AutomationWorker worker, Fake fake) { worker.Configure("1234", true); worker.Step(); fake.Now += 900; }
    static void Cleared(Fake fake) { foreach (var input in fake.Batch) Check(input.data.keyboard.scan == 0 && input.data.keyboard.vk == 0, "input zeroed"); }
    internal static int Execute()
    {
        try
        {
            byte[] original = Encoding.UTF8.GetBytes("test-only-1234");
            byte[] restored = ProtectedData.Unprotect(ProtectedData.Protect(original, null, DataProtectionScope.CurrentUser), null, DataProtectionScope.CurrentUser);
            if (Encoding.UTF8.GetString(restored) != "test-only-1234") return 1;
            if (Marshal.SizeOf(typeof(Native.INPUT)) != (IntPtr.Size == 8 ? 40 : 28)) return 2;
            if (!PinLabel.Matches("PIN") || !PinLabel.Matches("Enter your PIN") || PinLabel.Matches("Password")) return 3;
            if (!String.Equals(Native.ProcessPath((uint)Process.GetCurrentProcess().Id), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return 5;
            if (!MainForm.VerifyTrayLifecycle()) return 6;
            StartupTask.VerifyDefinition();
            SelfTests.Run();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.ToString()); return 4; }
    }

    internal static void Run()
    {
        OverrideTests();
        ToggleHotkeyTests();
        PacedInputTests();
        IdentityTests();
        EventTests();
        LatencyTests();
        DelayTests();
        WorkerSafetyTests();
        StorageTests();
        Benchmark();
        Console.WriteLine("PASS: worker safety, lifecycle, atomic writes, and existing checks.");
    }
    static void Benchmark()
    {
        uint pid = (uint)Process.GetCurrentProcess().Id;
        for (int mode = 0; mode < 2; mode++) {
            for (int i = 0; i < 50; i++) { if (mode == 0) LegacyPath(pid); else Native.ProcessPath(pid); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int collections = GC.CollectionCount(0);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++) { if (mode == 0) LegacyPath(pid); else Native.ProcessPath(pid); }
            Console.WriteLine("Process lookup {0}: 2000 calls, {1} ms, {2} Gen0 collections", mode == 0 ? "original" : "new (including creation time)", watch.ElapsedMilliseconds, GC.CollectionCount(0) - collections);
        }
    }
    static string LegacyPath(uint pid)
    {
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try {
            var path = new StringBuilder(32768); int length = path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)) throw new System.ComponentModel.Win32Exception();
            return path.ToString();
        } finally { CloseHandle(process); }
    }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
