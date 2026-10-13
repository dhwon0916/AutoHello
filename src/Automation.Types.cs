using System;

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
    bool WatchReadiness { get; }
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

