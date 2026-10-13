using System;
using System.Threading;

internal sealed partial class AutomationWorker
{
    Target pacedTarget;
    int pacedIndex, pacedEpoch;
    long pacedDue;

    // Each paced segment gets fresh eligibility checks. Waiting never holds the send lock.
    bool StepPaced()
    {
        Target target; int version, epoch;
        lock (gate) {
            if (pacedTarget == null) return false;
            // This prompt is already recorded. A deliberate skip must cancel only
            // its remaining input, even if the modifier also changed focus.
            if (SkipForOverride(pacedTarget, generation)) return true;
            // A focus event already invalidates this attempt; do not wait for the
            // next character deadline or call a potentially stalled provider.
            if (pacedEpoch != Thread.VolatileRead(ref focusEpoch)) {
                CancelPaced(generation);
                return true;
            }
            if (io.Milliseconds < pacedDue) return true;
            target = pacedTarget; version = generation; epoch = pacedEpoch;
        }
        bool sending = false;
        try {
            long started = io.Milliseconds;
            Target current = io.Inspect();
            bool foreground = target.Same(current) && io.StillForeground(target);
            lock (gate) {
                if (!Current(version) || pacedTarget != target) return true;
                if (SkipForOverride(target, version)) return true;
                if (!foreground || epoch != Thread.VolatileRead(ref focusEpoch) || io.Milliseconds - started > MaximumInspectionMilliseconds || io.ModifiersHeld) {
                    CancelPaced(version);
                    return true;
                }
                bool enter = pacedIndex == secret.Length;
                int characters = enter ? 0 : delays.CharacterInterval == 0 ? secret.Length : 1;
                var batch = CreateInputBatch(pacedIndex, characters, enter);
                try {
                    if (!Current(version)) return true;
                    if (SkipForOverride(target, version)) return true;
                    if (epoch != Thread.VolatileRead(ref focusEpoch) || io.ModifiersHeld) {
                        CancelPaced(version); return true;
                    }
                    sending = true;
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
        } catch (Exception error) {
            lock (gate) {
                if (Current(version) && pacedTarget == target) {
                    if (sending) StopPaced(version, "Paced input failed. Paused; inspect the prompt manually. No retry.");
                    else InspectionFailed(version, error);
                }
            }
        }
        return true;
    }

    void CancelPaced(int version)
    {
        // Focus/modifier changes cancel this recorded attempt, not the user's
        // enabled setting. The skip key may already be released by this point.
        pacedTarget = null; candidate = null; state = DetectionState.Watching;
        report(version, "Input interrupted. Complete this prompt manually; automatic entry remains on for other prompts. No retry for this prompt.", false, true);
    }

    void StopPaced(int version, string message)
    {
        ClearSecret();
        report(version, message, true, true);
    }

}
