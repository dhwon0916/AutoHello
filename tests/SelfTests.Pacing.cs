using System;
using System.IO;
using System.Threading;

internal static partial class SelfTests
{
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
                Check(worker.State == DetectionState.Watching && worker.NextDelay != Timeout.Infinite &&
                    interrupted.Sends == 1 && interrupted.Inspections == inspections, "focus cancellation keeps watching without provider calls");
                interrupted.InspectHook = null;
                worker.Step(); worker.Step();
                Check(interrupted.Sends == 1, "immediately canceled prompt cannot retry");
                interrupted.Target = NewTarget(2); worker.Step(); worker.Step(); worker.Step();
                Check(interrupted.Sends == 2, "next prompt works without re-enabling after focus cancellation");
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
                Check(fake.Sends == 1 && worker.State == (mode < 3 ? DetectionState.Watching : DetectionState.Paused), "paced cancellation stops remaining input with correct enabled state");
                if (mode < 3) {
                    fake.Foreground = true; fake.Held = false;
                    worker.Step(); worker.Step();
                    Check(fake.Sends == 1, "canceled prompt never resumes remaining input");
                    fake.Target = NewTarget(2); worker.Step(); worker.Step(); worker.Step();
                    Check(fake.Sends == 2, "focus or modifier interruption preserves entry for next prompt");
                }
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

}
