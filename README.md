# AutoHello

A small Windows desktop utility that tries to enter your locally configured PIN into an app/browser Windows Hello prompt.

## Use

1. Run `AutoHello.exe` and approve the Windows administrator prompt. If you decline, settings remain available but automatic entry is grayed out. Use **Restart as administrator** in settings or **Run as administrator** in the tray menu to try again.
2. Enter your preset PIN in the masked box. Click **Save PIN** to remember it, or leave it unsaved for this session.
3. Turn on **Enable automatic PIN entry**. You can close the settings window; the app continues in the notification tray.
4. Open your app/browser verification prompt. Select the PIN option and focus its empty PIN field if necessary. AutoHello waits briefly, then attempts entry once for that dialog.
5. Use the tray menu to pause automatic entry, open settings, or **Exit** the app. Double-click the tray icon to open settings. Closing or minimizing the settings window hides it to the tray.

Do not begin typing into the PIN field yourself while enabled: the app sends the preset PIN without clearing existing text. **Emptiness is not verified.** Protected UI Automation fields do not provide a reliable, general-purpose empty-state check. AutoHello never requests their value, text, or length. After an incorrect PIN, pause and complete the prompt manually, or close it and correct the preset before opening another prompt. There are no automatic retries. Automatic entry and Enter-key preferences are remembered; automatic entry resumes only when running as administrator with a saved PIN. A second instance at the same privilege level is prevented.

## Settings interface

Hover over a setting or advanced delay label for an explanation. Keyboard users can focus a control and press **F1** for the same help. Descriptions are also exposed to accessibility tools.

The compact, resizable window uses standard Windows controls for PIN storage, automatic entry, and timing. Settings scroll while the current state, diagnostic message, and tray/exit actions remain visible. **Hide to tray** keeps the app running; **Exit AutoHello** stops it. When administrator access is missing, the restart button stays available in the footer.

The PIN remains masked by default. **Show PIN** reveals it temporarily and resets when the window loses focus or is hidden. The storage label distinguishes a loaded/saved PIN from unsaved session edits. Editing the PIN pauses automatic entry; enable it again when ready. Advanced timing expands within the scroll area without resizing the window. **Reset to Balanced** restores the default delays.

## Start with Windows

While running as administrator, check **Start with Windows (as administrator)**. This registers a per-user Task Scheduler task named `AutoHello-<your Windows SID>` using your interactive sign-in token and highest privileges, with no account password stored. At sign-in it launches this executable with `--tray`, without another UAC prompt. Uncheck the option to remove the task. Startup remains off until you enable it.

Save your PIN and enable automatic entry before closing settings if you want entry to resume after sign-in. Keep the executable at the same path. A task pointing to another executable location is shown unchecked; checking startup from the new location replaces the per-user task. If elevation requires a different Windows account, that account has its own PIN storage and startup task.

## Typing speed

Under **Show advanced timing settings**, set **Time between characters** to control PIN typing speed (for example, 100 ms is about ten characters per second). **Wait before Enter** sets the delay after the final character before submission. Click **Apply delays** to save. Enter delay applies only when **Press Enter after the PIN** is checked. Both default to 0 ms, which preserves immediate batched input. Selecting a speed preset or resetting to Balanced also resets these two values to zero.

Paced entry rechecks the eligible field, process identity, foreground window, modifiers, and cancellation before each segment. Focus changes, input failures, or stale inspections stop the remaining input without retrying; a partial PIN may remain for manual completion. Pause, PIN edits, timing changes, and exit cancel pending segments. These checks cannot eliminate the race between checking focus and Windows processing input.

## Adjustable delays

Open settings as administrator and choose an **Entry speed** preset. Selecting a preset applies and saves it immediately:

- **Fast:** 100 ms stable-focus wait, 50 ms readiness checks, and 25 ms event spacing. Gives the prompt less time to settle.
- **Balanced (default):** 200 ms stable-focus wait, 100 ms readiness checks, and 50 ms event spacing.
- **Conservative:** 700 ms stable-focus wait; other intervals match Balanced. Allows more settling time but does not eliminate the focus-change race.

All three use a 2,000 ms idle interval, 200 ms stabilization-check interval, and 1,500 ms readiness window. Existing saved timings are preserved and displayed as the matching preset or **Custom**.

The individual controls are under **Advanced settings**, collapsed by default. Choose **Custom** to expand them without changing the active timings, edit **Delays (milliseconds)**, then click **Apply delays**. Values are saved with preferences. **Reset delays** applies Balanced and saves the defaults below; it does not change your PIN, Enter preference, startup task, or attempt history. Applying a preset, custom values, or reset cancels pending entry and starts a fresh stability period if automation is enabled.

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

All values support up to 2,147,483,647 ms. Zero stable-focus delay removes the settling wait but still requires a separate final eligibility inspection. Zero readiness window disables the short readiness burst; zero event spacing removes event throttling while retaining coalescing. Positive polling intervals prevent a zero-delay polling loop. Short intervals increase CPU use; long intervals delay detection and observe focus less often. Reducing the settling delay increases accidental-entry risk and cannot make Windows authentication instantaneous. The 300 ms stale-inspection limit, failure cooldown, modifier checks, and one-attempt rule remain fixed.

Older preference files load with default delays. Invalid, negative, overflowing, or zero polling intervals in saved settings are rejected. Self-tests cover custom-delay persistence, legacy defaults, zero delay with final validation, long delays, cancellation when timing changes, and reset without permitting retries.

## Compatibility

This is a best-effort utility, not a verified way to automate every Windows Hello prompt. It only recognizes a focused password edit field with the word `PIN` in its accessibility name, hosted by `%WINDIR%\System32\CredentialUIBroker.exe`. Other hosts, inaccessible fields, localized names without `PIN`, the lock screen, and secure desktop prompts are skipped. It does not switch authentication methods for you. A successful input API call does not prove authentication succeeded.

Windows updates released on or after January 13, 2026 can block automated credential input. AutoHello requests administrator privileges at launch and requires them for automatic entry. It does not change Windows security policies. See [Microsoft's compatibility notice](https://support.microsoft.com/en-us/servicing/os/windows/docs/2026/01/new-behavior-restricting-certain-applications-to-autofill-credentials-introduced-by-the-windows-janu).

If detection reports an access error or Windows blocks input, save your PIN and click **Restart as administrator**. Windows will ask for elevation. Re-enable entry in the restarted app and reopen the verification prompt. Microsoft documents elevation as a temporary compatibility mitigation; it is not guaranteed to make every prompt accessible. Unsaved PIN changes are not carried into the restarted process. Status messages distinguish a missing PIN focus, an unrecognized PIN label, and inspection errors; no PIN or field values appear in diagnostics.

## Storage

The saved PIN is protected using Windows DPAPI for the current Windows account and stored at `%LOCALAPPDATA%\AutoHello\pin.bin`. **Forget saved PIN** deletes it. There is no network communication or PIN logging. The PIN exists in application memory while open; account encryption does not protect it from software already running as you. Enabling automation allows any eligible prompt to use this PIN; the utility cannot verify the requesting website or app. Foreground checks reduce accidental entry, but a focus change during input remains possible.

PIN and preference writes use a unique temporary file in the same directory, flush it, then replace the destination (or move on first save). The PIN temporary file contains only DPAPI ciphertext. Failed replacement preserves the previous file; errors remain visible. There is no silent fallback to older credentials. Invalid or oversized saved data fails closed with a settings message. A crash can leave an unused `.tmp` file; replacement is not a guarantee against every filesystem or power-loss failure.

The worker keeps one erasable character array while enabled and creates an exact-size input array per batch (or per segment when pacing is enabled), clearing that array in `finally` even if sending throws. Pause and exit clear the worker copy. Managed strings returned by `TextBox.Text`, the native WinForms edit buffer, marshaling, and garbage collector copies cannot be reliably erased; masking and these cleanup measures are not a secure-memory guarantee.

See [automation internals](docs/automation.md) for detection, scheduling, cancellation, and attempt-history details.

## Source layout and performance

Application sources live in `src/`, regression checks in `tests/`, and implementation notes in `docs/`. Generated check builds go in the ignored `bin/` directory. The default executable stays at the repository root to preserve existing startup-task paths.

- `src/AutoHello.cs`: application entry point and elevation.
- `src/MainForm.cs`: settings coordination, tray lifecycle, and worker notifications.
- `src/MainForm.Layout.cs`: interface construction and styling; `tests/MainForm.Tests.cs`: isolated form checks.
- `src/Automation.cs`: detection state machine and typed dialog identities.
- `src/WindowsAutomation.cs`: UI Automation inspection, focus hooks, and PIN label matching.
- `src/Settings.cs`: delay settings, bounded reads, and atomic persistence.
- `src/StartupTask.cs`: Task Scheduler integration; `src/Native.cs`: native Windows interop.
- `tests/SelfTests.cs`: fake-provider regression tests and microbenchmarks.

Dialog history uses a value identity containing HWND, PID, and process creation time. Repeated comparisons no longer allocate formatted strings, and each inspection reuses its history lookup. A local 100,000-lookup microbenchmark measured 12 ms / 5 Gen0 collections for string keys and 1 ms / 0 collections for value keys. This does not measure overall application CPU use or authentication speed. Identity tests cover each key component and control recreation; existing cancellation, one-attempt, and history-capacity tests still apply. Form construction suspends outer layout, and label wrapping avoids assigning unchanged size constraints.

## Build and check

Run `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` from this folder. The execution policy option applies only to that build process. Uses the Windows .NET Framework C# compiler and UI Automation assemblies, with no downloads.

Build a separate executable and run all regression checks with one command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -OutputPath .\bin\AutoHello.checked.exe -Test
```

The script resolves sources relative to itself and creates the output directory. Relative output paths are resolved from your current directory. Test output is saved beside the executable as `self-test.out.txt` and `self-test.err.txt`; a failing test makes the build command fail. Tests remain compiled into the executable for the existing `--self-test` diagnostic command.

`AutoHello.exe --self-test` exits with code 0 when DPAPI round-trip, native input structure size, PIN label matching, process path lookup, tray close/exit behavior, privilege gating, and in-memory startup task definition checks pass. It also tests fake focus/process/clock/input providers: minimum delay, focus changes, slow/stale inspection, pause/resume generations, duplicate attempts, HWND/PID reuse, partial/failed input, buffer cleanup, and cancellation/shutdown during blocked inspection. Atomic write tests use disposable test files. It neither reads saved credentials nor sends keystrokes, requests elevation, or registers a task. The in-memory scheduler check needs permission to connect to the local Task Scheduler service.

If the normal executable is running, use the separate check build above. Exit the running tray app before rebuilding the normal executable for use.

Additional tests cover 1,000-event bursts, missed-event fallback, stale/out-of-order notices, focus events during inspection/final validation, PIN edits, recreated controls in attempted dialogs, held modifiers, subscription-thread ownership, stalled inspection cleanup, bounded cooldown, the 4,096-attempt limit, corrupt settings, and startup-path classification. All event and input tests use fake providers; live hook delivery and Hello metadata still require manual verification.

The self-test prints a 2,000-call current-process lookup microbenchmark. A local run measured original lookup at 28 ms / 41 Gen0 collections versus 21 ms / 0 Gen0 collections for the new lookup, including creation-time checks. A deterministic fake-clock test measured six inspections over ten idle seconds (including enable), compared with 34 for the original 300 ms schedule. These are a microbenchmark and a scheduling test, not live application CPU measurements. Reducing the initial character buffer from 32,768 to 512 is a 98.4% reduction in that buffer capacity, not a measured reduction in total allocations. Paused polling is eliminated; overall idle CPU improvement is expected, not measured.

## Manual verification

- Exit the old tray instance and rebuild the normal executable. Check close-to-tray, Open settings, pause/resume, PIN-edit pause, and Exit at both privilege levels.
- In an intentionally opened test prompt, leave the PIN field empty and avoid typing. Verify one attempt only and the Enter preference. Switch focus before the delay and confirm entry is deferred. Do not use unrelated sensitive windows to test the residual race.
- Verify foreground/focus event response, missed-event fallback, and postponed entry while a modifier is held; release it and allow a fresh stable delay. Check Exit while a prompt is present.
- After rejection or blocked input, verify no repeat after pause/resume and that the diagnostic remains visible. Complete the prompt manually; a same-process reused HWND may be skipped.
- Save/reload and forget a test PIN; check preferences and startup separately, including moving the executable and signing in. These live checks were not run by automated tests.

## Microsoft references

- [UI Automation threading and MTA lifetime](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading) and [managed UI Automation threading](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-threading-issues).
- [SetWinEventHook message-loop and callback requirements](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook), [UnhookWinEvent thread requirements](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwinevent), and [GetAsyncKeyState limitations](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate).
- [Edit control password/value restrictions](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-support-for-the-edit-control-type).
- [SendInput return values, UIPI, and keyboard state](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
- [Process creation time](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes) and [process image path](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew).
- [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=netframework-4.8.1), [FileStream.Flush](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=netframework-4.8.1), [Stopwatch](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.stopwatch?view=netframework-4.8.1), and [COM release ownership precautions](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.releasecomobject?view=netframework-4.8.1).
