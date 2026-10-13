using System;
using System.IO;
using System.Text;

internal static partial class SelfTests
{
    static void StorageTests()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AutoHello-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings");
        try {
            AtomicFile.Write(path, Encoding.UTF8.GetBytes("old")); AtomicFile.Write(path, Encoding.UTF8.GetBytes("new"));
            Check(File.ReadAllText(path) == "new", "atomic replacement");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
                bool failed = false;
                try { AtomicFile.Write(path, Encoding.UTF8.GetBytes("lost")); } catch (IOException) { failed = true; }
                Check(failed, "failed replacement surfaces error");
            }
            Check(File.ReadAllText(path) == "new" && Directory.GetFiles(directory).Length == 1, "failed replacement preserves old file and cleans temporary");
            bool savedEnabled, savedSubmit;
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(StoredSettings.Preferences(true, false)));
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit);
            Check(savedEnabled && !savedSubmit, "preferences round trip");
            DelaySettings timing;
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit, out timing);
            Check(timing.StableFocus == 200, "legacy preferences use default delays");
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(StoredSettings.Preferences(true, false, DelaySettings.FromValues(new[] { 0, 4000, 5000, 1, 0, Int32.MaxValue }))));
            StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit, out timing);
            Check(timing.StableFocus == 0 && timing.StabilizationInterval == 5000 && timing.EventSpacing == Int32.MaxValue, "custom delays persist");
            foreach (string corrupt in new[] { "", "enabled=maybe\nsubmit=true", "enabled=true\nsubmit=true\nextra=true", "enabled=true\nsubmit=true\ndelays=0,0,200,100,1500,50", "enabled=true\nsubmit=true\ndelays=0,1,1,1,0,2147483648", new string('x', 1025) }) {
                AtomicFile.Write(path, Encoding.UTF8.GetBytes(corrupt));
                bool rejected = false;
                try { StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "malformed/oversized preferences rejected");
            }
            AtomicFile.Write(path, new byte[] { 0xff });
            bool invalidUtf8 = false;
            try { StoredSettings.ReadPreferences(path, out savedEnabled, out savedSubmit); } catch (DecoderFallbackException) { invalidUtf8 = true; }
            Check(invalidUtf8, "invalid UTF8 preferences rejected");
            bool invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (System.Security.Cryptography.CryptographicException) { invalidPin = true; }
            Check(invalidPin, "corrupt synthetic ciphertext rejected");
            AtomicFile.Write(path, new byte[65537]); invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (InvalidDataException) { invalidPin = true; }
            Check(invalidPin, "oversized ciphertext rejected before decrypting");
            byte[] synthetic = Encoding.UTF8.GetBytes(new string('1', 128));
            AtomicFile.Write(path, System.Security.Cryptography.ProtectedData.Protect(synthetic, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            invalidPin = false;
            try { StoredSettings.ReadPin(path); } catch (InvalidDataException) { invalidPin = true; }
            Check(invalidPin, "oversized decrypted PIN rejected");
        } finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
        Check(StartupTask.Classify(true, 0, System.Windows.Forms.Application.ExecutablePath + ".old", "--tray") == StartupState.DifferentExecutable, "startup path mismatch");
        Check(StartupTask.Classify(false, 0, System.Windows.Forms.Application.ExecutablePath, "--tray") == StartupState.Disabled, "disabled startup");
    }

}
