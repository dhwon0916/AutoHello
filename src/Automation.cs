using System;
using System.Collections.Generic;
using System.Threading;

// Value identity avoids allocating formatted strings during every inspection.
// All three fields matter: Windows can reuse handles and process IDs.
internal struct DialogIdentity : IEquatable<DialogIdentity>
{
    readonly IntPtr window;
    readonly uint process;
    readonly long created;
    internal DialogIdentity(IntPtr window, uint process, long created)
    {
        this.window = window; this.process = process; this.created = created;
    }
    public bool Equals(DialogIdentity other)
    {
        return window == other.window && process == other.process && created == other.created;
    }
    public override bool Equals(object value) { return value is DialogIdentity && Equals((DialogIdentity)value); }
    public override int GetHashCode()
    {
        unchecked { return ((window.GetHashCode() * 397) ^ process.GetHashCode()) * 397 ^ created.GetHashCode(); }
    }
}

// All UIA objects stay on the single MTA worker. Snapshots contain identity only.
internal sealed class Target
{
    internal IntPtr Window;
    internal uint Process;
    internal long Created;
    internal int[] Field;
    internal DialogIdentity Dialog { get { return new DialogIdentity(Window, Process, Created); } }
    internal bool Same(Target other)
    {
        if (other == null || !Dialog.Equals(other.Dialog) || Field == null || other.Field == null ||
            Field.Length == 0 || Field.Length != other.Field.Length) return false;
        for (int i = 0; i < Field.Length; i++)
            if (Field[i] != other.Field[i]) return false;
        return true;
    }
}

internal interface IAutomationIO
{
    long Milliseconds { get; }
    string Status { get; }
    bool ModifiersHeld { get; }
    bool OverrideHeld(OverrideKey key);
    Target Inspect();
    bool StillForeground(Target target);
    uint Send(Native.INPUT[] input);
}

internal interface IFocusEvents
{
    void Subscribe(Action changed);
    void Unsubscribe();
}

internal enum DetectionState { Paused, Watching, Stabilizing, Attempted }

internal sealed class AutomationWorker : IDisposable
{
    DelaySettings delays = new DelaySettings();
    readonly object gate = new object();
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly IAutomationIO io;
    readonly Action<int, string, bool, bool> report;
    readonly Thread thread;
    readonly HashSet<DialogIdentity> attempted = new HashSet<DialogIdentity>();
    char[] secret;
    Target pacedTarget;
    int pacedIndex, pacedEpoch;
    long pacedDue;
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
            candidate = null; state = DetectionState.Watching; pending = true; wake.Set();
        }
    }
    internal int NextDelay {
        get { lock (gate) {
            if (stopped || secret == null) return Timeout.Infinite;
            if (pacedTarget != null) return pacedEpoch != Thread.VolatileRead(ref focusEpoch)
                ? 0 : (int)Math.Max(0, Math.Min(Int32.MaxValue, pacedDue - io.Milliseconds));
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
    // Use the same bounded history for attempts and deliberate skips, including
    // across settings changes. Never clear text or synthesize modifier releases.
    bool SkipForOverride(Target target, int version)
    {
        if (target == null || overrideKey == OverrideKey.None || !io.OverrideHeld(overrideKey)) return false;
        if (!attempted.Contains(target.Dialog) && attempted.Count >= 4096) {
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
        try {
            long started = io.Milliseconds;
            Target target = io.Inspect();
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                failures = 0;
                if (target != null && io.Milliseconds - started <= 300 && !attempted.Contains(target.Dialog) && SkipForOverride(target, version)) return;
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
                if (target == null || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
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
                if (!target.Same(final) || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
            }
            // Process checks can block too: keep them outside the configuration lock.
            bool foreground = io.StillForeground(target);
            lock (gate) {
                if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) return;
                if (!foreground || io.Milliseconds - started > 300) { candidate = null; state = DetectionState.Watching; return; }
                if (SkipForOverride(target, version)) return;
                if (io.ModifiersHeld) { candidate = null; state = DetectionState.Watching; return; }
                if (attempted.Count >= 4096) {
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
                Native.INPUT[] batch = new Native.INPUT[secret.Length * 2 + (submit ? 2 : 0)];
                try {
                    for (int i = 0; i < secret.Length; i++) {
                        batch[i * 2] = Native.Key(0, secret[i], 4);
                        batch[i * 2 + 1] = Native.Key(0, secret[i], 6);
                    }
                    if (submit) { batch[batch.Length - 2] = Native.Key(13, '\0', 0); batch[batch.Length - 1] = Native.Key(13, '\0', 2); }
                    if (!Current(version) || epoch != Thread.VolatileRead(ref focusEpoch)) { candidate = null; state = DetectionState.Watching; return; }
                    if (SkipForOverride(target, version)) return;
                    if (io.ModifiersHeld) { candidate = null; state = DetectionState.Watching; return; }
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
    // Each paced segment gets fresh eligibility checks. Waiting never holds the send lock.
    bool StepPaced()
    {
        Target target; int version, epoch;
        lock (gate) {
            if (pacedTarget == null) return false;
            // A focus event already invalidates this attempt; do not wait for the
            // next character deadline or call a potentially stalled provider.
            if (pacedEpoch != Thread.VolatileRead(ref focusEpoch)) {
                StopPaced(generation, "Input interrupted because focus changed. Paused; complete the prompt manually. No retry.");
                return true;
            }
            if (io.Milliseconds < pacedDue) return true;
            target = pacedTarget; version = generation; epoch = pacedEpoch;
        }
        try {
            long started = io.Milliseconds;
            Target current = io.Inspect();
            bool foreground = target.Same(current) && io.StillForeground(target);
            lock (gate) {
                if (!Current(version) || pacedTarget != target) return true;
                if (foreground && epoch == Thread.VolatileRead(ref focusEpoch) && io.Milliseconds - started <= 300 && SkipForOverride(target, version)) return true;
                if (!foreground || epoch != Thread.VolatileRead(ref focusEpoch) || io.Milliseconds - started > 300 || io.ModifiersHeld) {
                    StopPaced(version, "Input interrupted because focus or readiness changed. Paused; complete the prompt manually. No retry.");
                    return true;
                }
                bool enter = pacedIndex == secret.Length;
                int characters = enter ? 0 : delays.CharacterInterval == 0 ? secret.Length : 1;
                var batch = new Native.INPUT[enter ? 2 : characters * 2];
                try {
                    if (enter) { batch[0] = Native.Key(13, '\0', 0); batch[1] = Native.Key(13, '\0', 2); }
                    else for (int i = 0; i < characters; i++) {
                        batch[i * 2] = Native.Key(0, secret[pacedIndex + i], 4);
                        batch[i * 2 + 1] = Native.Key(0, secret[pacedIndex + i], 6);
                    }
                    if (!Current(version)) return true;
                    if (epoch == Thread.VolatileRead(ref focusEpoch) && SkipForOverride(target, version)) return true;
                    if (epoch != Thread.VolatileRead(ref focusEpoch) || io.ModifiersHeld) {
                        StopPaced(version, "Input interrupted. Paused; complete the prompt manually. No retry."); return true;
                    }
                    uint sent = io.Send(batch);
                    if (sent != batch.Length) {
                        StopPaced(version, "Input blocked or partial. Paused; complete the prompt manually. No retry."); return true;
                    }
                    pacedIndex += characters;
                    if (enter || (pacedIndex == secret.Length && !submit)) {
                        pacedTarget = null;
                        report(version, "Input sent once. Authentication success is not verified. No retry for this dialog.", false, true);
                    } else pacedDue = io.Milliseconds + (pacedIndex == secret.Length ? delays.EnterDelay : delays.CharacterInterval);
                } finally { Array.Clear(batch, 0, batch.Length); }
            }
        } catch (Exception) {
            lock (gate) {
                if (Current(version) && pacedTarget == target) {
                    failures = Math.Min(failures + 1, 4);
                    notBefore = io.Milliseconds + (1000 << (failures - 1));
                    StopPaced(version, "Paced input failed. Paused; inspect the prompt manually. No retry.");
                }
            }
        }
        return true;
    }

    void StopPaced(int version, string message)
    {
        ClearSecret();
        report(version, message, true, true);
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
