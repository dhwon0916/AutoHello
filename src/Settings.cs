using System;

internal static class AtomicFile
{
    internal static void Write(string path, byte[] data)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var stream = new System.IO.FileStream(temporary, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None)) {
                stream.Write(data, 0, data.Length); stream.Flush(true);
            }
            if (System.IO.File.Exists(path)) System.IO.File.Replace(temporary, path, null);
            else System.IO.File.Move(temporary, path);
        } finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
    }
}

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
            for (int i = 0; i < 3; i++) {
                int[] expected = Preset(i).Values, actual = Values;
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

internal static class StoredSettings
{
    internal static byte[] ReadBounded(string path, int maximum)
    {
        using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read)) {
            if (stream.Length == 0 || stream.Length > maximum) throw new System.IO.InvalidDataException();
            using (var reader = new System.IO.BinaryReader(stream)) {
                int length = (int)stream.Length;
                byte[] data = reader.ReadBytes(length);
                if (data.Length != length) throw new System.IO.InvalidDataException();
                return data;
            }
        }
    }
    internal static string ReadPin(string path)
    {
        byte[] plain = System.Security.Cryptography.ProtectedData.Unprotect(ReadBounded(path, 65536), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try {
            string value = new System.Text.UTF8Encoding(false, true).GetString(plain);
            if (value.Length == 0 || value.Length > 127) throw new System.IO.InvalidDataException();
            return value;
        } finally { Array.Clear(plain, 0, plain.Length); }
    }
    internal static string Preferences(bool enabled, bool submit, DelaySettings delays = null)
    {
        string result = "enabled=" + (enabled ? "true" : "false") + "\nsubmit=" + (submit ? "true" : "false") + "\n";
        if (delays != null) {
            delays.Validate();
            result += "delays=" + String.Join(",", Array.ConvertAll(delays.Values, delegate(int value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); })) + "\n";
        }
        return result;
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit)
    {
        DelaySettings ignored;
        ReadPreferences(path, out enabled, out submit, out ignored);
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit, out DelaySettings delays)
    {
        delays = new DelaySettings();
        string value = new System.Text.UTF8Encoding(false, true).GetString(ReadBounded(path, 1024));
        using (var reader = new System.IO.StringReader(value.TrimStart('\uFEFF'))) {
            string first = reader.ReadLine(), second = reader.ReadLine();
            if ((first != "enabled=true" && first != "enabled=false") || (second != "submit=true" && second != "submit=false"))
                throw new System.IO.InvalidDataException();
            string third = reader.ReadLine();
            if (third != null) {
                if (!third.StartsWith("delays=", StringComparison.Ordinal) || reader.ReadLine() != null) throw new System.IO.InvalidDataException();
                string[] parts = third.Substring(7).Split(',');
                if (parts.Length != 6 && parts.Length != 8) throw new System.IO.InvalidDataException();
                int[] values = new int[parts.Length];
                for (int i = 0; i < values.Length; i++)
                    if (!Int32.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out values[i])) throw new System.IO.InvalidDataException();
                delays = DelaySettings.FromValues(values);
            }
            enabled = first == "enabled=true"; submit = second == "submit=true";
        }
    }
}
