using System;
using System.Collections.Generic;
using System.Threading;

internal sealed partial class AutomationWorker : IDisposable
{
    const int MaximumAttempts = 4096;
    const int MaximumInspectionMilliseconds = 300;
    const int OverridePollMilliseconds = 50;
    DelaySettings delays = new DelaySettings();
    readonly object gate = new object();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly IAutomationIO io;
    readonly Action<int, string, bool, bool> report;
    readonly Thread thread;
    readonly HashSet<DialogIdentity> attempted = new HashSet<DialogIdentity>();
    char[] secret;
    bool submit, stopped;
    OverrideKey overrideKey;
    int generation;
    int cancellation;
    int focusEpoch;
    int candidateEpoch;
    int failures;
    long notBefore, lastInspection = -50;
    long readinessUntil;
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
            readinessUntil = io.Milliseconds + delays.ReadinessWindow;
            candidate = null; state = DetectionState.Watching;
            if (!pending) { pending = true; wake.Set(); }
        }
    }
    internal int NextDelay {
        get { lock (gate) {
            if (stopped || secret == null) return Timeout.Infinite;
            if (pacedTarget != null) return pacedEpoch != Thread.VolatileRead(ref focusEpoch)
                ? 0 : (int)Math.Max(0, Math.Min(overrideKey == OverrideKey.None ? Int32.MaxValue : OverridePollMilliseconds, pacedDue - io.Milliseconds));
            long due = pending ? lastInspection + delays.EventSpacing : lastInspection + (state == DetectionState.Stabilizing ? delays.StabilizationInterval : delays.IdleInterval);
            // A foreground/focus event often precedes creation of the PIN edit.
            // Briefly bridge that gap without increasing steady-state idle polling.
            if (!pending && state == DetectionState.Watching && io.Milliseconds < readinessUntil)
                due = Math.Min(due, lastInspection + delays.ReadinessInterval);
            if (!pending && state == DetectionState.Stabilizing && candidate != null)
                due = Math.Min(due, since + delays.StableFocus);
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
    internal int Configure(string pin, bool enter, DelaySettings timing = null, OverrideKey temporaryOverride = OverrideKey.None)
    {
        DelaySettings next = timing == null ? new DelaySettings() : timing.Copy();
        next.Validate();
        if (!Enum.IsDefined(typeof(OverrideKey), temporaryOverride)) throw new ArgumentOutOfRangeException("temporaryOverride");
        // Publish cancellation before waiting for the short send/configuration lock.
        int requested = Interlocked.Increment(ref cancellation);
        lock (gate) {
            if (stopped) return generation;
            ClearSecret();
            secret = String.IsNullOrEmpty(pin) ? null : pin.ToCharArray();
            submit = enter;
            overrideKey = temporaryOverride;
            delays = next;
            generation = requested;
            readinessUntil = 0;
            candidate = null; state = secret == null ? DetectionState.Paused : DetectionState.Watching;
            pending = secret != null; lastInspection = io.Milliseconds - delays.EventSpacing;
            wake.Set();
            return requested;
        }
    }
    void ClearSecret() { pacedTarget = null; if (secret != null) Array.Clear(secret, 0, secret.Length); secret = null; state = DetectionState.Paused; }
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
                // A capped wait is also a polling deadline (temporary override).
                // Recomputing the delay alone would never sample the key until
                // the full character/Enter deadline had elapsed.
                if (delay == 0 || !wake.WaitOne(delay)) Step();
            }
        } finally {
            try { if (events != null) events.Unsubscribe(); }
            catch (Exception) { /* Shutdown cannot safely wait/retry a failed COM removal. */ }
            finally { wake.Dispose(); }
        }
    }
    bool Current(int version) { return !stopped && secret != null && generation == version && Thread.VolatileRead(ref cancellation) == version; }
    // Use the same bounded history for attempts and deliberate skips, including
    // across settings changes. Never clear text or synthesize modifier releases.
    bool SkipForOverride(Target target, int version)
    {
        if (target == null || overrideKey == OverrideKey.None || !io.OverrideHeld(overrideKey)) return false;
        if (!attempted.Contains(target.Dialog) && attempted.Count >= MaximumAttempts) {
            ClearSecret();
            report(version, "Attempt history is full. Paused. Close all credential prompts before restarting AutoHello.", true, true);
            return true;
        }
        attempted.Add(target.Dialog);
        pacedTarget = null; candidate = null; state = DetectionState.Attempted;
        report(version, "Temporary override: skipped this prompt. Complete it manually; other prompts remain enabled.", false, true);
        return true;
    }
    internal void Step()
    {
        lock (gate) { if (stopped || secret == null) return; }
        if (StepPaced()) return;
        int version, epoch;
        lock (gate) {
            if (stopped || secret == null || io.Milliseconds < notBefore) return;
            version = Thread.VolatileRead(ref generation); epoch = Thread.VolatileRead(ref focusEpoch);
            pending = false; lastInspection = io.Milliseconds;
        }
        bool sending = false;
        try {
            long started = io.Milliseconds;
            Target target = io.Inspect();
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                failures = 0;
                // Keep the short readiness burst only for a verified broker.
                // Other foreground apps need only events and idle fallback.
                if (target == null && !io.WatchReadiness) readinessUntil = 0;
                if (target != null && io.Milliseconds - started <= MaximumInspectionMilliseconds && !attempted.Contains(target.Dialog) && SkipForOverride(target, version)) return;
                bool modifiers = io.ModifiersHeld;
                bool alreadyAttempted = target != null && attempted.Contains(target.Dialog);
                string notice = alreadyAttempted
                    ? "Already attempted or skipped this dialog identity. No retry, including after pause/resume. Complete the prompt manually."
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
                if (target == null || io.Milliseconds - started > MaximumInspectionMilliseconds) { candidate = null; state = DetectionState.Watching; return; }
                if (alreadyAttempted) { candidate = null; state = DetectionState.Attempted; return; }
                if (modifiers) { candidate = null; state = DetectionState.Watching; return; }
                if (candidateGeneration != version || candidateEpoch != epoch || !target.Same(candidate) || io.Milliseconds - lastObservation > Math.Max(1000L, (long)delays.StabilizationInterval + 1000)) {
                    state = DetectionState.Stabilizing;
                    candidate = target; candidateGeneration = version; candidateEpoch = epoch; since = lastObservation = io.Milliseconds; return;
                }
                lastObservation = io.Milliseconds;
                if (io.Milliseconds - since < delays.StableFocus) return;
            }
            started = io.Milliseconds;
            Target final = io.Inspect();
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                if (!target.Same(final) || io.Milliseconds - started > MaximumInspectionMilliseconds) { candidate = null; state = DetectionState.Watching; return; }
            }
            // Process checks can block too: keep them outside the configuration lock.
            bool foreground = io.StillForeground(target);
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                if (!foreground || io.Milliseconds - started > MaximumInspectionMilliseconds) { candidate = null; state = DetectionState.Watching; return; }
                if (SkipForOverride(target, version)) return;
                if (io.ModifiersHeld) { candidate = null; state = DetectionState.Watching; return; }
                if (attempted.Count >= MaximumAttempts) {
                    ClearSecret();
                    report(version, "Attempt history is full. Paused. Close all credential prompts before restarting AutoHello.", true, true);
                    return;
                }
                // Retain tombstones even after destruction: same-process HWND reuse is ambiguous.
                if (delays.CharacterInterval > 0 || (submit && delays.EnterDelay > 0)) {
                    if (!attempted.Add(target.Dialog)) return;
                    pacedTarget = target; pacedIndex = 0; pacedEpoch = epoch; pacedDue = io.Milliseconds;
                    candidate = null; state = DetectionState.Attempted;
                    return;
                }
                Native.INPUT[] batch = CreateInputBatch(0, secret.Length, submit);
                try {
                    if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) { candidate = null; state = DetectionState.Watching; return; }
                    if (SkipForOverride(target, version)) return;
                    if (io.ModifiersHeld) { candidate = null; state = DetectionState.Watching; return; }
                    if (!attempted.Add(target.Dialog)) return;
                    candidate = null; state = DetectionState.Attempted;
                    // Configuration and shutdown serialize with this short native send, never with UIA.
                    sending = true;
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
                if (!sending) { InspectionFailed(version, error); return; }
                ClearSecret();
                report(version, "Input failed: " + error.GetType().Name + " (0x" + error.HResult.ToString("X8") + "). Paused; complete the prompt manually. No retry.", true, true);
            }
        }
    }
    void InspectionFailed(int version, Exception error)
    {
        // A disappearing UIA element or inaccessible foreground process is not
        // an input failure. Discard the inspection and retry detection later.
        // Any paced attempt already has a tombstone, so it cannot be resumed.
        pacedTarget = null; candidate = null; state = DetectionState.Watching;
        failures = Math.Min(failures + 1, 4);
        notBefore = io.Milliseconds + (1000 << (failures - 1));
        report(version, "Detection temporarily unavailable: " + error.GetType().Name + " (0x" + error.HResult.ToString("X8") + "). Automatic entry remains on; detection will retry after a short delay. Interrupted prompts will not be retried.", false, true);
    }

    // Call under gate; callers clear the returned buffer in a finally block.
    Native.INPUT[] CreateInputBatch(int offset, int characters, bool enter)
    {
        var batch = new Native.INPUT[characters * 2 + (enter ? 2 : 0)];
        for (int i = 0; i < characters; i++) {
            batch[i * 2] = Native.Key(0, secret[offset + i], 4);
            batch[i * 2 + 1] = Native.Key(0, secret[offset + i], 6);
        }
        if (enter) {
            batch[batch.Length - 2] = Native.Key(13, '\0', 0);
            batch[batch.Length - 1] = Native.Key(13, '\0', 2);
        }
        return batch;
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
