using System;
using System.Threading;

internal static partial class SelfTests
{
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
            Check(fake.Inspections == 7 && worker.NextDelay == 100, "1000 events coalesced into one inspection and bounded readiness checks");
            fake.Now += 1500; worker.Step();
            Check(worker.NextDelay == 2000, "readiness burst expires to idle fallback");
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
            fake.ForegroundHook = null; worker.Configure("5678", false); worker.Step(); fake.Now += 199; worker.Step();
            Check(fake.Sends == 0, "PIN edit restarts stability"); fake.Now += 1; worker.Step();
            Check(fake.Sends == 1 && fake.FirstScan == '5' && worker.State == DetectionState.Attempted, "edited PIN only after new delay");
            fake.Target.Field = new[] { 1, 44 }; worker.SignalFocusChange(); worker.Step(); fake.Now += 900; worker.Step();
            Check(fake.Sends == 1 && worker.State == DetectionState.Attempted, "control recreation cannot retry dialog");
        }
        fake = new Fake { Held = true };
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            Arm(worker, fake); worker.Step(); Check(fake.Sends == 0, "held modifier postpones input");
            fake.Held = false; worker.Step(); fake.Now += 900;
            fake.ForegroundHook = delegate { fake.Held = true; }; worker.Step(); Check(fake.Sends == 0, "late modifier prevents send");
            fake.ForegroundHook = null; fake.Held = false; worker.Step(); fake.Now += 199; worker.Step(); Check(fake.Sends == 0, "modifier release restarts delay");
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
    static void DelayTests()
    {
        var fake = new Fake();
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            var timing = new DelaySettings { StableFocus = 0 };
            worker.Configure("1234", false, timing); worker.Step();
            Check(worker.NextDelay == 0 && fake.Sends == 0, "zero delay still requires final inspection");
            worker.Step(); Check(fake.Sends == 1, "zero delay sends after revalidation");
            worker.Configure("1234", false, new DelaySettings()); worker.Step();
            Check(fake.Sends == 1, "reset cannot retry attempted dialog");
            fake.Target = NewTarget(2);
            timing = new DelaySettings { StableFocus = 10000, StabilizationInterval = 5000 };
            worker.Configure("1234", false, timing); timing.StableFocus = 0;
            worker.Step(); fake.Now += worker.NextDelay; worker.Step();
            Check(fake.Sends == 1, "worker owns timing snapshot and long delay");
            fake.Now += worker.NextDelay; worker.Step(); Check(fake.Sends == 2, "long check interval does not continually restart stability");
            fake.Target = NewTarget(3); worker.Configure("1234", false, new DelaySettings()); worker.Step();
            fake.Now += 100; worker.Configure("1234", false, new DelaySettings()); worker.Step();
            fake.Now += 199; worker.Step(); Check(fake.Sends == 2, "delay change resets pending entry");
            fake.Now++; worker.Step(); Check(fake.Sends == 3, "default reset timing");
        }
    }
    static void LatencyTests()
    {
        var fake = new Fake { Target = null };
        using (var worker = new AutomationWorker(fake, delegate { }, false)) {
            worker.Configure("1234", false); worker.Step();
            fake.Now = 10; worker.SignalFocusChange();
            fake.Now += worker.NextDelay; worker.Step(); // Event before field creation.
            fake.Target = NewTarget(1);
            fake.Now += worker.NextDelay; worker.Step();
            long detected = fake.Now;
            while (fake.Sends == 0) {
                int delay = worker.NextDelay;
                Check(delay > 0 && delay <= 200, "bounded stabilization schedule");
                fake.Now += delay; worker.Step();
                Check(fake.Now <= detected + 200, "no extra stability tick");
            }
            Check(detected == 150 && fake.Now == 350, "prompt ready after event sends at 350ms");
            Console.WriteLine("Fake delayed-field latency: 350 ms from timeline start (700 ms stability version: 850 ms); stable delay is 200 ms.");
        }
    }
}
