using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Threading;

internal static partial class SelfTests
{
    sealed class Fake : IAutomationIO, IFocusEvents
    {
        internal long Now;
        internal Target Target = NewTarget(1);
        internal bool Foreground = true, ThrowSend, Held;
        internal OverrideKey HeldOverride;
        internal char FirstScan;
        internal int Inspections, Sends;
        internal uint? Sent;
        internal Action InspectHook, ForegroundHook;
        internal Action FocusEvent, OnUnsubscribe;
        int eventThread;
        internal Native.INPUT[] Batch;
        public long Milliseconds { get { return Now; } }
        public string Status { get { return "fake status"; } }
        public bool ModifiersHeld { get { return Held || HeldOverride != OverrideKey.None; } }
        public bool OverrideHeld(OverrideKey key) { return key != OverrideKey.None && HeldOverride == key; }
        public void Subscribe(Action changed) { eventThread = Thread.CurrentThread.ManagedThreadId; FocusEvent = changed; }
        public void Unsubscribe() {
            Check(eventThread == Thread.CurrentThread.ManagedThreadId, "unsubscribe on subscribing thread");
            FocusEvent = null; if (OnUnsubscribe != null) OnUnsubscribe();
        }
        public Target Inspect() { Inspections++; if (InspectHook != null) InspectHook(); return Target; }
        public bool StillForeground(Target target) { if (ForegroundHook != null) ForegroundHook(); return Foreground; }
        public uint Send(Native.INPUT[] input) { Sends++; Batch = input; FirstScan = (char)input[0].data.keyboard.scan; if (ThrowSend) throw new InvalidOperationException(); return Sent ?? (uint)input.Length; }
    }
    static void PacedInputTests()
    {
        for (int mode = 0; mode < 2; mode++) {
            var interrupted = new Fake();
            using (var worker = new AutomationWorker(interrupted, delegate { }, false)) {
                worker.Configure("12", true, new DelaySettings { StableFocus = 0,
                    CharacterInterval = mode == 0 ? Int32.MaxValue : 0, EnterDelay = Int32.MaxValue });
                worker.Step(); worker.Step(); worker.Step();
                Check(interrupted.Sends == 1 && worker.NextDelay == Int32.MaxValue, "long paced delay scheduled");
                int inspections = interrupted.Inspections;
                worker.SignalFocusChange();
                Check(worker.NextDelay == 0, "focus cancellation bypasses character and Enter delays");
                interrupted.InspectHook = delegate { throw new Exception("Canceled entry must not inspect"); };
                worker.Step();
                Check(worker.State == DetectionState.Paused && worker.NextDelay == Timeout.Infinite &&
                    interrupted.Sends == 1 && interrupted.Inspections == inspections, "focus cancellation pauses without provider calls");
                interrupted.InspectHook = null;
                worker.Configure("12", true, new DelaySettings { StableFocus = 0 });
                worker.Step(); worker.Step();
                Check(interrupted.Sends == 1, "immediately canceled prompt cannot retry");
            }
        }
        var fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Configure("12", true, new DelaySettings { StableFocus = 0, CharacterInterval = 100, EnterDelay = 250 });
            worker.Step(); worker.Step(); worker.Step();
            Check(fake.Sends == 1 && fake.FirstScan == '1' && fake.Batch.Length == 2, "paced first character"); Cleared(fake);
            fake.Now = 99; worker.Step(); Check(fake.Sends == 1 && worker.NextDelay == 1, "character delay enforced");
            fake.Now = 100; worker.Step(); Check(fake.Sends == 2 && fake.FirstScan == '2', "paced second character"); Cleared(fake);
            fake.Now = 349; worker.Step(); Check(fake.Sends == 2, "Enter delay enforced");
            fake.Now = 350; worker.Step(); Check(fake.Sends == 3 && fake.FirstScan == '\0', "delayed Enter"); Cleared(fake);
            worker.Configure("12", true); worker.Step(); fake.Now += 1000; worker.Step();
            Check(fake.Sends == 3, "paced attempt cannot retry");
        }
        for (int mode = 0; mode < 5; mode++) {
            fake = new Fake();
            using (var worker = new AutomationWorker(fake, delegate { }, false)) {
                worker.Configure("12", true, new DelaySettings { StableFocus = 0, CharacterInterval = 100 });
                worker.Step(); worker.Step(); worker.Step();
                if (mode == 0) worker.SignalFocusChange();
                if (mode == 1) fake.Foreground = false;
                if (mode == 2) fake.Held = true;
                if (mode == 3) worker.Configure(null, false);
                if (mode == 4) worker.Dispose();
                fake.Now = 100; worker.Step();
                Check(fake.Sends == 1 && worker.State == DetectionState.Paused, "paced cancellation stops remaining input");
            }
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Configure("12", true, new DelaySettings { StableFocus = 0, EnterDelay = 50 });
            worker.Step(); worker.Step(); worker.Step();
            Check(fake.Sends == 1 && fake.Batch.Length == 4, "zero character interval batches PIN");
            fake.Sent = 0; fake.Now = 50; worker.Step();
            Check(fake.Sends == 2 && worker.State == DetectionState.Paused, "failed paced Enter pauses"); Cleared(fake);
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Configure("1", false, new DelaySettings { StableFocus = 0, CharacterInterval = 50, EnterDelay = 100 });
            worker.Step(); worker.Step(); worker.Step(); fake.Now = 1000; worker.Step();
            Check(fake.Sends == 1, "Enter preference respected with pacing");
        }
        string path = Path.Combine(Path.GetTempPath(), "AutoHello-pacing-" + Guid.NewGuid().ToString("N"));
        try {
            File.WriteAllText(path, StoredSettings.Preferences(false, true, new DelaySettings { CharacterInterval = 123, EnterDelay = 456 }));
            bool enabled, submit; DelaySettings restored;
            StoredSettings.ReadPreferences(path, out enabled, out submit, out restored);
            Check(restored.CharacterInterval == 123 && restored.EnterDelay == 456, "pacing persistence");
            File.WriteAllText(path, "enabled=false\nsubmit=true\ndelays=200,2000,200,100,1500,50\n");
            StoredSettings.ReadPreferences(path, out enabled, out submit, out restored);
            Check(restored.CharacterInterval == 0 && restored.EnterDelay == 0, "legacy pacing defaults");
        } finally { if (File.Exists(path)) File.Delete(path); }
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
    static void WorkerSafetyTests()
    {
        var fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Step(); Check(fake.Inspections == 0, "paused has no inspections");
            Arm(worker, fake); fake.Now = 199; worker.Step(); Check(fake.Sends == 0, "minimum stable delay");
            fake.Now = 200; worker.Step(); Check(fake.Sends == 1 && fake.Batch.Length == 10, "one Unicode batch with Enter"); Cleared(fake);
            worker.Step(); worker.Configure(null, false); worker.Configure("1234", true); worker.Step(); fake.Now += 900; worker.Step();
            Check(fake.Sends == 1, "no retry after pause/resume");
            fake.Target = null; worker.Step(); fake.Target = NewTarget(1); worker.Step(); fake.Now += 900; worker.Step();
            Check(fake.Sends == 1, "ambiguous same-process HWND reuse blocked");
            fake.Target = NewTarget(2); worker.Step(); fake.Now += 900; worker.Step(); Check(fake.Sends == 2, "PID/HWND reuse with new process lifetime");
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); fake.Foreground = false; worker.Step(); Check(fake.Sends == 0, "foreground change");
            fake.Foreground = true; worker.Step(); fake.Now += 900;
            int finalCall = fake.Inspections + 2;
            fake.InspectHook = delegate { if (fake.Inspections == finalCall) fake.Target = new Target { Window = new IntPtr(42), Process = 77, Created = 1, Field = new[] { 1, 43 } }; };
            worker.Step(); Check(fake.Sends == 0, "final focus change");
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); fake.InspectHook = delegate { fake.Now += 301; }; worker.Step(); Check(fake.Sends == 0, "slow stale inspection");
            fake.InspectHook = delegate { worker.Configure(null, false); }; worker.Step(); Check(fake.Sends == 0, "pause during inspection");
            fake.InspectHook = null; Arm(worker, fake);
            fake.ForegroundHook = delegate { worker.Configure(null, false); worker.Configure("5678", false); };
            worker.Step(); Check(fake.Sends == 0, "configuration generation rejects stale results");
        }
        for (int mode = 0; mode < 3; mode++) {
            fake = new Fake { Sent = mode == 0 ? 0u : 3u, ThrowSend = mode == 2 };
            bool paused = false;
            using (var worker = new AutomationWorker(fake, delegate(int version, string message, bool pause, bool durable) { if (pause) paused = true; }, false)) {
                Arm(worker, fake); worker.Step(); Check(paused && fake.Sends == 1, "partial/failed input pauses"); Cleared(fake);
                worker.Configure("1234", true); worker.Step(); fake.Now += 900; worker.Step(); Check(fake.Sends == 1, "failed send is not retried");
            }
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); fake.Now += 2000; worker.Step(); Check(fake.Sends == 0, "long observation gap restarts stability");
            fake.Now += 900;
            int finalCall = fake.Inspections + 2;
            fake.InspectHook = delegate { if (fake.Inspections == finalCall) fake.Now += 301; };
            worker.Step(); Check(fake.Sends == 0, "slow final inspection discarded");
            fake.InspectHook = null; worker.Configure("1234", false); worker.Step(); fake.Now += 900; worker.Step();
            Check(fake.Sends == 1 && fake.Batch.Length == 8, "Enter disabled"); Cleared(fake);
        }
        fake = new Fake();
        int reports = 0;
        var closed = new AutomationWorker(fake, delegate { reports++; }, false);
        Arm(closed, fake); fake.InspectHook = delegate { closed.Dispose(); }; closed.Step(); closed.Step();
        Check(fake.Sends == 0 && reports == 1, "shutdown rejects in-flight result and report"); closed.Dispose();
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var returned = new ManualResetEvent(false)) {
            fake = new Fake();
            fake.InspectHook = delegate { Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA, "MTA worker"); entered.Set(); release.WaitOne(); returned.Set(); };
            var worker = new AutomationWorker(fake, delegate { }, true);
            worker.Configure("1234", false); Check(entered.WaitOne(3000), "worker started");
            var watch = Stopwatch.StartNew(); worker.Configure(null, false); worker.Dispose();
            Check(watch.ElapsedMilliseconds < 500, "shutdown/pause does not wait on provider");
            release.Set(); Check(returned.WaitOne(3000), "provider released");
        }
    }

    static void StorageTests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AutoHello-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings");
        try {
            AtomicFile.Write(path, Encoding.UTF8.GetBytes("old")); AtomicFile.Write(path, Encoding.UTF8.GetBytes("new"));
            Check(File.ReadAllText(path) == "new", "atomic replacement");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                bool failed = false;
                try { AtomicFile.Write(path, Encoding.UTF8.GetBytes("lost")); } catch (IOException) { failed = true; }
                Check(failed, "failed replacement surfaces error");
            }
            Check(File.ReadAllText(path) == "new" && Directory.GetFiles(directory).Length == 1, "failed replacement preserves old file and cleans temporary");
            bool savedEnabled, savedSubmit;
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(StoredSettings.Preferences(true, false)));
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit);
            Check(savedEnabled && !savedSubmit, "preferences round trip");
            DelaySettings timing;
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit, out timing);
            Check(timing.StableFocus == 200, "legacy preferences use default delays");
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(StoredSettings.Preferences(true, false, DelaySettings.FromValues(new[] { 0, 4000, 5000, 1, 0, Int32.MaxValue }))));
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit, out timing);
            Check(timing.StableFocus == 0 && timing.StabilizationInterval == 5000 && timing.EventSpacing == Int32.MaxValue, "custom delays persist");
            foreach (string corrupt in new[] { "", "enabled=maybe\nsubmit=true", "enabled=true\nsubmit=true\nextra=true", "enabled=true\nsubmit=true\ndelays=0,0,200,100,1500,50", "enabled=true\nsubmit=true\ndelays=0,1,1,1,0,2147483648", new string('x', 1025) }) {
                AtomicFile.Write(path, Encoding.UTF8.GetBytes(corrupt));
                bool rejected = false;
                try { StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "malformed/oversized preferences rejected");
            }
            AtomicFile.Write(path, new byte[] { 0xff });
            bool invalidUtf8 = false;
            try { StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit); } catch (DecoderFallbackException) { invalidUtf8 = true; }
            Check(invalidUtf8, "invalid UTF8 preferences rejected");
            bool invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (System.Security.Cryptography.CryptographicException) { invalidPin = true; }
            Check(invalidPin, "corrupt synthetic ciphertext rejected");
            AtomicFile.Write(path, new byte[65537]); invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (InvalidDataException) { invalidPin = true; }
            Check(invalidPin, "oversized ciphertext rejected before decrypting");
            byte[] synthetic = Encoding.UTF8.GetBytes(new string('1', 128));
            AtomicFile.Write(path, System.Security.Cryptography.ProtectedData.Protect(synthetic, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (InvalidDataException) { invalidPin = true; }
            Check(invalidPin, "oversized decrypted PIN rejected");
        } finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
        Check(StartupTask.Classify(true, 0, System.Windows.Forms.Application.ExecutablePath + ".old", "--tray") == StartupState.DifferentExecutable, "startup path mismatch");
        Check(StartupTask.Classify(false, 0, System.Windows.Forms.Application.ExecutablePath, "--tray") == StartupState.Disabled, "disabled startup");
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
