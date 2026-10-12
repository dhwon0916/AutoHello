using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class SelfTests
{
    sealed class Fake : IAutomationIO, IFocusEvents
    {
        internal long Now;
        internal Target Target = NewTarget(1);
        internal bool Foreground = true, ThrowSend, Held;
        internal char FirstScan;
        internal int Inspections, Sends;
        internal uint? Sent;
        internal Action InspectHook, ForegroundHook;
        internal Action FocusEvent, OnUnsubscribe;
        int eventThread;
        internal Native.INPUT[] Batch;
        public long Milliseconds { get { return Now; } }
        public string Status { get { return "fake status"; } }
        public bool ModifiersHeld { get { return Held; } }
        public void Subscribe(Action changed) { eventThread = Thread.CurrentThread.ManagedThreadId; FocusEvent = changed; }
        public void Unsubscribe() {
            Check(eventThread == Thread.CurrentThread.ManagedThreadId, "unsubscribe on subscribing thread");
            FocusEvent = null; if (OnUnsubscribe != null) OnUnsubscribe();
        }
        public Target Inspect() { Inspections++; if (InspectHook != null) InspectHook(); return Target; }
        public bool StillForeground(Target target) { if (ForegroundHook != null) ForegroundHook(); return Foreground; }
        public uint Send(Native.INPUT[] input) { Sends++; Batch = input; FirstScan = (char)input[0].data.keyboard.scan; if (ThrowSend) throw new InvalidOperationException(); return Sent ?? (uint)input.Length; }
    }
    static Target NewTarget(long created) { return new Target { Window = new IntPtr(42), Process = 77, Created = created, Field = "pin-field" }; }
    static void Check(bool result, string name) { if (!result) throw new InvalidOperationException("Self-test: " + name); }
    static void Arm(AutomationWorker worker, Fake fake) { worker.Configure("1234", true); worker.Step(); fake.Now += 900; }
    static void Cleared(Fake fake) { foreach (var input in fake.Batch) Check(input.data.keyboard.scan == 0 && input.data.keyboard.vk == 0, "input zeroed"); }
    internal static void Run()
    {
        EventTests();
        var fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Step(); Check(fake.Inspections == 0, "paused has no inspections");
            Arm(worker, fake); fake.Now = 699; worker.Step(); Check(fake.Sends == 0, "minimum stable delay");
            fake.Now = 700; worker.Step(); Check(fake.Sends == 1 && fake.Batch.Length == 10, "one Unicode batch with Enter"); Cleared(fake);
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
            fake.InspectHook = delegate { if (fake.Inspections == finalCall) fake.Target = new Target { Window = new IntPtr(42), Process = 77, Created = 1, Field = "other-field" }; };
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
            foreach (string corrupt in new[] { "", "enabled=maybe\nsubmit=true", "enabled=true\nsubmit=true\nextra=true", new string('x', 1025) }) {
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
        Benchmark();
        Console.WriteLine("PASS: worker safety, lifecycle, atomic writes, and existing checks.");
    }
    static void EventTests()
    {
        var bounded = new Fake();
        bool capacityPaused = false;
        using (var worker = new AutomationWorker(bounded, delegate(int version, string message, bool pause, bool durable) { if (pause) capacityPaused = true; }, false)) {
            worker.Configure("1234", false);
            for (int i = 0; i <= 4096; i++) {
                bounded.Target = NewTarget(i + 1); worker.Step(); bounded.Now += 900; worker.Step();
            }
            Check(bounded.Sends == 4096 && capacityPaused && worker.State == DetectionState.Paused, "attempt capacity pauses without eviction");
            worker.Configure("1234", false); bounded.Target = NewTarget(1); worker.Step(); bounded.Now += 900; worker.Step();
            Check(bounded.Sends == 4096, "oldest attempted dialog retained at capacity");
        }
        var fake = new Fake { Target = null };
        int notices = 0, noticeVersion = 0; string noticeText = null;
        using (var worker = new AutomationWorker(fake, delegate(int version, string message, bool pause, bool durable) { notices++; noticeVersion = version; noticeText = message; }, false)) {
            Check(worker.State == DetectionState.Paused && worker.NextDelay == Timeout.Infinite, "paused schedule");
            worker.Configure("1234", false); Check(worker.NextDelay == 0, "enable inspects immediately");
            for (int time = 0; time <= 10000; time += 2000) {
                fake.Now = time; Check(worker.NextDelay == 0, "missed-event fallback due"); worker.Step();
                Check(worker.NextDelay == 2000, "low frequency fallback");
            }
            Check(fake.Inspections == 6, "fallback inspection count");
            Check(notices == 1, "identical statuses suppressed");
            Console.WriteLine("Fake idle schedule: 6 inspections over 10 seconds including enable (old 300 ms schedule: 34).");
            for (int i = 0; i < 1000; i++) worker.SignalFocusChange();
            Check(!worker.IsLatestNotice(noticeVersion, noticeText), "queued stale focus notice rejected");
            Check(worker.NextDelay == 50, "burst throttled"); fake.Now += 50; worker.Step();
            Check(fake.Inspections == 7 && worker.NextDelay == 2000, "1000 events coalesced into one inspection");
            worker.Configure(null, false); worker.SignalFocusChange(); Check(worker.NextDelay == Timeout.Infinite, "paused events do not schedule inspections");
            Check(!worker.IsLatestNotice(noticeVersion, noticeText), "out-of-order configuration notice rejected");
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); Check(worker.State == DetectionState.Stabilizing, "stabilizing state");
            fake.InspectHook = delegate { worker.SignalFocusChange(); };
            worker.Step(); Check(fake.Sends == 0 && worker.State == DetectionState.Watching, "focus event invalidates active result");
            fake.InspectHook = null; worker.Step(); fake.Now += 900;
            fake.ForegroundHook = delegate { worker.SignalFocusChange(); };
            worker.Step(); Check(fake.Sends == 0, "late focus event rejects final result");
            fake.ForegroundHook = null; worker.Configure("5678", false); worker.Step(); fake.Now += 699; worker.Step();
            Check(fake.Sends == 0, "PIN edit restarts stability"); fake.Now += 1; worker.Step();
            Check(fake.Sends == 1 && fake.FirstScan == '5' && worker.State == DetectionState.Attempted, "edited PIN only after new delay");
            fake.Target.Field = "recreated-control"; worker.SignalFocusChange(); worker.Step(); fake.Now += 900; worker.Step();
            Check(fake.Sends == 1 && worker.State == DetectionState.Attempted, "control recreation cannot retry dialog");
        }
        fake = new Fake { Held = true };
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); worker.Step(); Check(fake.Sends == 0, "held modifier postpones input");
            fake.Held = false; worker.Step(); fake.Now += 900;
            fake.ForegroundHook = delegate { fake.Held = true; }; worker.Step(); Check(fake.Sends == 0, "late modifier prevents send");
            fake.ForegroundHook = null; fake.Held = false; worker.Step(); fake.Now += 699; worker.Step(); Check(fake.Sends == 0, "modifier release restarts delay");
            fake.Now++; worker.Step(); Check(fake.Sends == 1, "entry after release and full delay");
        }
        fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            fake.InspectHook = delegate { throw new InvalidOperationException(); };
            for (int i = 0; i < 6; i++) {
                worker.Configure("1234", false); worker.Step();
                int expected = 1000 << Math.Min(i, 3);
                worker.Configure("1234", false); Check(worker.NextDelay == expected, "bounded cooldown survives manual re-enable");
                int count = fake.Inspections; worker.Step(); Check(fake.Inspections == count, "cooldown cannot be bypassed");
                fake.Now += expected;
            }
        }
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var second = new ManualResetEvent(false))
        using (var unsubscribed = new ManualResetEvent(false)) {
            fake = new Fake();
            fake.OnUnsubscribe = delegate { unsubscribed.Set(); };
            fake.InspectHook = delegate { if (fake.Inspections == 1) { entered.Set(); release.WaitOne(); } else second.Set(); };
            var worker = new AutomationWorker(fake, delegate { }, true);
            try {
                worker.Configure("1234", false); Check(entered.WaitOne(3000), "stalled inspection entered");
                for (int i = 0; i < 1000; i++) fake.FocusEvent();
                Check(fake.Inspections == 1, "one active inspection while stalled");
                fake.Now = 1000;
                release.Set(); Check(second.WaitOne(3000), "one pending inspection after stalled result discarded");
                Check(fake.Sends == 0, "stalled stale result never sends");
            } finally { worker.Dispose(); release.Set(); }
            Check(unsubscribed.WaitOne(3000), "event subscriptions removed after inspection returns");
        }
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
