using System;

internal enum OverrideKey { None, Control, Alt, Windows, Shift }

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
    internal static string Preferences(bool enabled, bool submit, DelaySettings delays = null, OverrideKey overrideKey = OverrideKey.None, ToggleKey? toggleKey = null)
    {
        string result = "enabled=" + (enabled ? "true" : "false") + "\nsubmit=" + (submit ? "true" : "false") + "\n";
        if (delays != null) {
            delays.Validate();
            result += "delays=" + String.Join(",", Array.ConvertAll(delays.Values, delegate(int value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); })) + "\n";
        }
        if (!Enum.IsDefined(typeof(OverrideKey), overrideKey)) throw new System.IO.InvalidDataException();
        if (overrideKey != OverrideKey.None) result += "override=" + overrideKey + "\n";
        ToggleKey shortcut = toggleKey ?? ToggleKey.ControlAltH;
        shortcut.Validate();
        result += "toggle=" + shortcut.Serialize() + "\n";
        return result;
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit)
    {
        DelaySettings ignored;
        ReadPreferences(path, out enabled, out submit, out ignored);
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit, out DelaySettings delays)
    {
        OverrideKey ignored;
        ReadPreferences(path, out enabled, out submit, out delays, out ignored);
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit, out DelaySettings delays, out OverrideKey overrideKey)
    {
        ToggleKey ignored;
        ReadPreferences(path, out enabled, out submit, out delays, out overrideKey, out ignored);
    }
    internal static void ReadPreferences(string path, out bool enabled, out bool submit, out DelaySettings delays, out OverrideKey overrideKey, out ToggleKey toggleKey)
    {
        delays = new DelaySettings();
        overrideKey = OverrideKey.None;
        toggleKey = ToggleKey.ControlAltH;
        string value = new System.Text.UTF8Encoding(false, true).GetString(ReadBounded(path, 1024));
        using (var reader = new System.IO.StringReader(value.TrimStart('\uFEFF'))) {
            string first = reader.ReadLine(), second = reader.ReadLine();
            if ((first != "enabled=true" && first != "enabled=false") || (second != "submit=true" && second != "submit=false"))
                throw new System.IO.InvalidDataException();
            string third = reader.ReadLine();
            if (third != null && third.StartsWith("delays=", StringComparison.Ordinal)) {
                string[] parts = third.Substring(7).Split(',');
                if (parts.Length != 6 && parts.Length != 8) throw new System.IO.InvalidDataException();
                int[] values = new int[parts.Length];
                for (int i = 0; i < values.Length; i++)
                    if (!Int32.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out values[i])) throw new System.IO.InvalidDataException();
                delays = DelaySettings.FromValues(values);
                third = reader.ReadLine();
            }
            if (third != null && third.StartsWith("override=", StringComparison.Ordinal)) {
                if (!Enum.TryParse<OverrideKey>(third.Substring(9), out overrideKey) ||
                    !Enum.IsDefined(typeof(OverrideKey), overrideKey) || third.Substring(9) != overrideKey.ToString()) throw new System.IO.InvalidDataException();
                third = reader.ReadLine();
            }
            if (third != null) {
                if (!third.StartsWith("toggle=", StringComparison.Ordinal) ||
                    !ToggleKey.TryParse(third.Substring(7), out toggleKey) ||
                    reader.ReadLine() != null) throw new System.IO.InvalidDataException();
            }
            enabled = first == "enabled=true"; submit = second == "submit=true";
        }
    }
}
