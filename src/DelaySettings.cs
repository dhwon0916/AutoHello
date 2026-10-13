using System;

internal sealed class DelaySettings
{
    internal int StableFocus = 200, IdleInterval = 2000, StabilizationInterval = 200,
        ReadinessInterval = 100, ReadinessWindow = 1500, EventSpacing = 50, CharacterInterval = 0, EnterDelay = 0;
    internal DelaySettings Copy() { return (DelaySettings)MemberwiseClone(); }
    internal static DelaySettings Preset(int index)
    {
        if (index == 0) return new DelaySettings { StableFocus = 100, ReadinessInterval = 50, EventSpacing = 25 };
        if (index == 1) return new DelaySettings();
        if (index == 2) return new DelaySettings { StableFocus = 700 };
        throw new ArgumentOutOfRangeException("index");
    }
    internal int PresetIndex {
        get {
            int[] actual = Values;
            for (int i = 0; i < 3; i++) {
                int[] expected = Preset(i).Values;
                bool same = true;
                for (int j = 0; j < actual.Length; j++) if (actual[j] != expected[j]) same = false;
                if (same) return i;
            }
            return 3;
        }
    }
    internal int[] Values { get { return new[] { StableFocus, IdleInterval, StabilizationInterval, ReadinessInterval, ReadinessWindow, EventSpacing, CharacterInterval, EnterDelay }; } }
    internal static DelaySettings FromValues(int[] values)
    {
        if (values == null || (values.Length != 6 && values.Length != 8)) throw new System.IO.InvalidDataException("Invalid delay count.");
        var result = new DelaySettings { StableFocus = values[0], IdleInterval = values[1], StabilizationInterval = values[2], ReadinessInterval = values[3], ReadinessWindow = values[4], EventSpacing = values[5], CharacterInterval = values.Length > 6 ? values[6] : 0, EnterDelay = values.Length > 7 ? values[7] : 0 };
        result.Validate(); return result;
    }
    internal void Validate()
    {
        int[] values = Values;
        for (int i = 0; i < values.Length; i++)
            if (values[i] < ((i == 1 || i == 2 || i == 3) ? 1 : 0)) throw new System.IO.InvalidDataException("Invalid delay.");
    }
}
