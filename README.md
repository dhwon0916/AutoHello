# AutoHello

A small Windows utility that enters your saved PIN into eligible app/browser Windows Hello prompts.

## Use

1. Run `AutoHello.exe` as administrator.
2. Enter your PIN, check **Automatic PIN entry**, and click **Save**.
3. Open your app/browser verification prompt and focus its empty PIN field. AutoHello attempts entry once per dialog.
4. Close the settings window or click **Hide** to keep running in the tray. Use **Exit** to stop.

Keep the Hello PIN field empty and do not type while automatic entry is on. Emptiness is not verified; the app never reads protected field contents. After an incorrect PIN, complete the prompt manually or reopen it after correcting your saved PIN. There are no automatic retries.

## Settings

The compact window contains PIN, General, Entry speed, and Hotkeys sections, with one **Save** button at the bottom. Hover over controls for help. Long status messages are available in the status tooltip.

**Save** stores the PIN, enabled state, Enter preference, startup choice, delays, and both hotkeys. Changes remain pending until Save. Editing the PIN or turning entry off pauses input immediately. The tray menu and toggle shortcut operate immediately using the last saved PIN and settings; they do not save pending edits. Closing the window keeps drafts in memory; exiting discards them. Click Save to remember the current enabled state for the next launch.

The PIN stays masked. **Show PIN** resets when the window loses focus or is hidden. Clear the PIN field and click Save to remove the saved PIN. Saving with an empty PIN requires automatic entry to be off.

Saving pauses pending input, then applies the saved configuration. If a write or startup change fails, the app reports the failure and remains paused; some earlier parts of the save may already have succeeded. An unavailable new hotkey leaves the old binding active and stops the save before writing settings.

## Hotkeys

Hotkeys are the final settings section.

- **Skip current prompt:** choose Ctrl, Alt, Windows, Shift, or None. Hold that modifier while the Hello PIN field is focused until the status reports it skipped. Release does not re-enable that prompt. Other prompts remain eligible. Either left or right modifier works.
- **Toggle on / off:** defaults to **Ctrl + Alt + H**. Click the field and press your own key or combination with Ctrl, Alt, Shift, and/or Windows. Press **Esc** to set **None**, then click **Save**. Tab and Shift+Tab navigate between controls.

The toggle works while the app is hidden in the tray. Holding it does not repeatedly toggle. While its shortcut field is focused, the key is captured instead of toggling entry. Shortcuts reserved by Windows or another app may be unavailable. Existing custom shortcuts are preserved; older settings without a toggle value get the default. Explicitly saved None stays disabled.

Using the toggle hotkey or tray toggle shows a Windows notification confirming that automatic PIN entry is on or off. If no saved PIN is available, the notification says entry is off and asks you to save a PIN. Windows notification settings may suppress these notifications.

The temporary override is checked during eligibility inspections, so brief taps between checks can be missed. Hold it before typing begins. During paced entry it stops remaining characters and Enter; any characters already sent remain. Other held modifiers postpone entry until release. Attempted and skipped prompts remain blocked across settings changes and pause/resume until AutoHello exits.

## Timing

Choose **Fast** (100 ms stable focus), **Balanced** (200 ms), **Conservative** (700 ms), or expand **Advanced timing** for custom delays. Click **Save** to apply. Selecting Balanced restores the default timing values.

| Setting | Default | Minimum |
| --- | ---: | ---: |
| Stable focus before entry | 200 ms | 0 ms |
| Idle fallback interval | 2,000 ms | 1 ms |
| Stable-focus check interval | 200 ms | 1 ms |
| Prompt readiness check interval | 100 ms | 1 ms |
| Prompt readiness window | 1,500 ms | 0 ms |
| Event burst spacing | 50 ms | 0 ms |
| Time between characters | 0 ms | 0 ms |
| Wait before Enter | 0 ms | 0 ms |

Fast also uses 50 ms readiness checks and 25 ms event spacing. All values support up to 2,147,483,647 ms. Presets reset character and Enter delays to zero. Enter delay applies only when **Press Enter after PIN** is checked. Paced entry rechecks focus, identity, modifiers, and cancellation before each segment. A focus change or input failure stops remaining input; a partial PIN can remain.

## Start with Windows

Check **Start with Windows**, then **Save**. This registers a per-user Task Scheduler task named `AutoHello-<your Windows SID>` using the interactive sign-in token and highest privileges, with no account password stored. It launches this executable with `--tray`. Uncheck and save to remove the task. Keep the executable at the same path.

## Compatibility and storage

AutoHello recognizes focused password edit fields with `PIN` in their accessibility name, hosted by `%WINDIR%\System32\CredentialUIBroker.exe`. It skips other hosts, inaccessible fields, the lock screen, and secure desktop prompts. It does not switch authentication methods. Successful input does not prove authentication succeeded. Focus checks cannot eliminate the race between validation and Windows processing input.

Windows updates released on or after January 13, 2026 can restrict automated credential input. AutoHello requests administrator privileges and does not change security policies. See [Microsoft's compatibility notice](https://support.microsoft.com/en-us/servicing/os/windows/docs/2026/01/new-behavior-restricting-certain-applications-to-autofill-credentials-introduced-by-the-windows-janu).

The PIN is protected with Windows DPAPI for the current user and stored at `%LOCALAPPDATA%\AutoHello\pin.bin`. Preferences are stored alongside it. There is no network communication or PIN logging. The PIN exists in application memory; account encryption does not protect it from software running as you. The app cannot verify the requesting website or app. Individual file writes use flushed temporary files and atomic replacement; the overall Save operation across PIN, preferences, and Task Scheduler is not a transaction.

See [automation internals](docs/automation.md) for detection, scheduling, cancellation, and bounded attempt history.

## Build and checks

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` to build `AutoHello.exe`. Exit the running tray instance first. The build uses the installed Windows .NET Framework compiler and UI Automation assemblies, with no downloads.

Build and run regression checks on the same executable (exit the tray app first):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

Tests use fake focus, input, save, and startup providers; no credentials are read, keys sent, or startup tasks registered. The existing in-memory Task Scheduler definition check requires access to the local service. Checks cover global Save staging and failures, PIN cleanup, default/custom/disabled shortcut persistence, conflicts and cleanup, tray lifecycle, timing, cancellation, process identity, one-attempt rules, and bounded settings reads. Output is saved in `bin/test-results/`.

Manual checks: verify custom shortcut capture and Esc, Save/reload, hotkey conflicts, tray toggling, startup registration, and one-attempt behavior in an intentionally opened Hello prompt. Live Windows Hello and global keyboard delivery are not covered by fake-provider tests.

## Source layout

- `src/Program.cs`: application startup, elevation, and single-instance handling.
- `src/MainForm.cs`, `MainForm.Layout.cs`, and `MainForm.Settings.cs`: window lifecycle, layout, and settings editing/saving.
- `src/Automation.cs`, `WindowsAutomation.cs`, and `Native.cs`: worker scheduling, Windows accessibility, and native input.
- `src/DelaySettings.cs`, `Settings.cs`, `ToggleHotkey.cs`, and `StartupTask.cs`: timing, storage, shortcuts, and Windows startup integration.
- `tests/`: shared self-test runner and safety/storage checks, separate hotkey and scheduling suites, and form tests.
- `docs/`: automation design notes. `bin/`: ignored generated artifacts and test results.

The build compiles all C# files directly under `src/` and `tests/`. The main executable remains at the project root by default; `-OutputPath` selects another location.
