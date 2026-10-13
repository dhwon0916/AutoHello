using System;
using System.Diagnostics;
using System.Threading;

internal static partial class SelfTests
{
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

}
