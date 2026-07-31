# FocusFlow

A minimalist desktop utility for managing study/work sessions and breaks, designed to
integrate seamlessly with macOS and Windows.

FocusFlow runs from the macOS menu bar / Windows system tray. Closing the window hides it
to the tray and keeps the timer running; the app only quits from the tray menu.

---

## Status

Working cross-platform build. The timer core, persistence and session history are done and
covered by tests. The tray/menu-bar shell, global hotkeys, packaging and localization are
not yet started — see [Not implemented](#not-implemented).

| | |
|---|---|
| Tests | 66 passing (xUnit + `FakeTimeProvider`) |
| Builds | Debug + Release, both target frameworks, 0 warnings |
| Verified on | macOS (Apple Silicon) |
| Not verified | **The entire Windows runtime path** — see [Caveats](#caveats) |

---

## Features

**Sessions**
- Start / stop / pause / resume / reset, plus **Skip** to advance manually
- Study 1–120 min (default 25), break 1–60 min (default 5)
- Standalone break, without running a study session first
- **Infinite mode**, or a finite run of 1–10 sessions
- Auto-start the next break and/or study session, configurable independently

**Alerts**
- Native notifications — Action Center toasts on Windows, notification banners on macOS
- Alarm sound with volume control, choosing from built-in system sounds or your own
  WAV/MP3 file
- Optional music when a break ends

**Persistence** — all local, no network, no account
- Settings survive restarts and are versioned for a future import/export
- An interrupted session is restored on the next launch, always paused
- Every finished session is appended to a history log for later reporting

**Platform**
- Launch at login (per-user; no administrator rights)
- Light / dark / follow-system theme
- Survives sleep, wake and system clock changes without losing time
- HiDPI aware; the window reopens on whichever monitor you're using

---

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone <repo-url> && cd FocusFlow
dotnet build FocusFlow.slnx
```

Run it (pick the target framework for your OS):

```bash
dotnet run --project FocusFlow.App -f net10.0
```

```bash
dotnet run --project FocusFlow.App -f net10.0-windows10.0.17763.0
```

Run the tests:

```bash
dotnet test FocusFlow.Domain.Tests/FocusFlow.Domain.Tests.csproj
```

Publish:

```bash
dotnet publish FocusFlow.App -c Release -f net10.0-windows10.0.17763.0 -r win-x64 --self-contained false
```

```bash
dotnet publish FocusFlow.App -c Release -f net10.0 -r osx-arm64 --self-contained false
```

> **Note:** launch-at-login does nothing under `dotnet run` — `Environment.ProcessPath`
> points at the dotnet host rather than the app, so registering it would pin the wrong
> binary. The app detects this and says so. Test it from a published build.

---

## Architecture

Clean Architecture with MVVM. Dependencies point inward; `FocusFlow.Domain` references
nothing.

```
FocusFlow.Domain          Timer engine, models. No dependencies.
FocusFlow.Application     Service interfaces + orchestration. Depends on Domain.
FocusFlow.Infrastructure  JSON storage. Depends on Application.
FocusFlow.App             Avalonia UI, DI, platform code. Depends on all.
FocusFlow.Domain.Tests    xUnit.
```

Platform-specific code is confined to `FocusFlow.App/Platforms/{Windows,MacOS}/` and
selected per target framework by the `WINDOWS` compiler symbol. Everything else is shared.

### Multi-targeting

`FocusFlow.App` targets both `net10.0` (macOS/Linux) and `net10.0-windows10.0.17763.0`.

The OS-versioned Windows TFM isn't cosmetic: `Microsoft.Toolkit.Uwp.Notifications` ships
`ToastNotificationManagerCompat` **only** in its `net*-windows10.0.17763` asset, so a plain
`net10.0-windows` target would compile and then fail at runtime.

`EnableWindowsTargeting` is set so both frameworks build from macOS or Linux. It's ignored
on Windows.

### Why the timer engine looks the way it does

`TimerEngine` polls every 200 ms but **recomputes** the remaining time from a monotonic
timestamp taken when the session started, rather than subtracting a second per tick. Three
things fall out of that:

- A delayed or coalesced callback can't accumulate drift.
- Changing the system clock — DST, a timezone move, an NTP correction — can't reach the
  countdown, because nothing reads wall-clock time to decide how much is left.
- A gap of more than 5 seconds between polls means the process was starved, i.e. the
  machine slept. That time is credited back rather than charged to your session, and works
  regardless of whether the platform's monotonic clock freezes during sleep.

`TimeProvider` is injected, so tests drive the clock deterministically instead of sleeping.
State is handed out as immutable snapshots, since the engine ticks on a timer thread while
the UI reads on the dispatcher thread.

---

## Data files

Stored under `%APPDATA%\FocusFlow\` on Windows and `~/.config/FocusFlow/` on macOS.
Nothing leaves the machine.

| File | Purpose |
|---|---|
| `config.json` | Settings. Versioned, indented, safe to hand-edit. |
| `session.json` | The in-flight session, for crash recovery. Deleted on a clean stop. |
| `history.jsonl` | Append-only log of finished sessions. |

`history.jsonl` is [JSON Lines](https://jsonlines.org) — one self-contained object per line:

```json
{"SchemaVersion":1,"Id":"563ad8ee…","Mode":"Study","Outcome":"Completed","StartedAt":"2026-07-30T09:00:00+00:00","EndedAt":"2026-07-30T09:25:00+00:00","PlannedDuration":"00:25:00","ActualDuration":"00:25:00","SessionNumber":1}
```

Chosen over a single JSON array because appending never rewrites earlier records, a process
killed mid-write can only damage the final line (which is skipped on read), and any tool can
stream it a line at a time. Enums are written as **names**, not ordinals, so a record stays
readable if the enum changes. Timestamps are UTC so a DST shift can't reorder the log.

Both planned and actual durations are kept, and `Outcome` distinguishes `Completed`,
`Skipped` and `Stopped` — enough to derive totals and completion rates later without
re-deriving anything from current settings. Stopping part-way through still records the
time spent; an immediate start-then-stop is discarded as a misclick.

---

## Platform implementations

| | macOS | Windows |
|---|---|---|
| Tray / menu bar | Avalonia `TrayIcon` (`NSStatusItem`) | Avalonia `TrayIcon` (`Shell_NotifyIcon`) |
| Notifications | `osascript` | `ToastNotificationManagerCompat` |
| Audio | `afplay -v` | MCI (`mciSendString`, winmm) |
| Launch at login | `~/Library/LaunchAgents` plist | `HKCU\…\CurrentVersion\Run` |
| Pointer position | CoreGraphics `CGEventGetLocation` | `GetCursorPos` |
| Sleep detection | Poll-gap heuristic (shared) | Poll-gap heuristic (shared) |

Two deliberate deviations from the obvious choices:

- **Windows audio uses MCI, not `System.Media.SoundPlayer`.** SoundPlayer is WAV-only with
  no volume control, so it cannot satisfy the MP3 + volume requirement. MCI's `mpegvideo`
  device handles both and ships with Windows, which avoids taking a dependency on
  NAudio or LibVLC. Windows *system-sound aliases* still go through `PlaySound` and play at
  system volume — the slider only affects file-based sounds.
- **The tray is Avalonia's `TrayIcon`, not WinForms `NotifyIcon`.** One implementation for
  both platforms, and no WinForms message pump running inside an Avalonia app.

---

## Not implemented

Known gaps against the full specification:

- **Persistent countdown in the tray.** The remaining time is currently a tooltip, shown on
  hover. Windows tray icons have no text label (the digits would have to be rendered into
  the icon bitmap); macOS `NSStatusItem.title` can show text but Avalonia doesn't expose it.
- **Global hotkeys** — needs `RegisterHotKey` on Windows and an Accessibility-permission
  monitor on macOS.
- **Lightweight popup** — clicking the tray opens the full window, not a compact popup.
  No progress bar; the tray context menu only offers Open and Quit.
- **Launch minimized** — the window is shown on first launch.
- Logging, localization, accessibility (screen reader / high contrast), single-instance
  enforcement, auto-update, packaging (MSIX/DMG) and code signing.
- No CI pipeline.
- Website blocking and cloud sync are **out of scope** for this version.

---

## Caveats

- **The Windows runtime path has never been executed.** Everything compiles and publishes,
  but MCI audio, the Run key, toasts, `GetCursorPos` and the DPI manifest were only built
  from macOS. Smoke-test these first on a real Windows machine.
- MCI's `mpegvideo` device is missing on Windows N editions without the Media Feature Pack;
  MP3 falls back to the alias beep there.
- On a **mixed Retina / non-Retina** macOS setup, the active-monitor calculation can pick a
  neighbouring display, so the window opens on the wrong monitor. Correct when all displays
  share a scale factor.
- `Assets/tray-icon.png` is a generated placeholder. For macOS it should be replaced with a
  monochrome template image so it adapts to the light/dark menu bar.
- Performance targets (memory, CPU, startup) have not been measured.

---

## Tech stack

C# / .NET 10 · Avalonia 12.1 · CommunityToolkit.Mvvm · Microsoft.Extensions.DependencyInjection ·
System.Text.Json · xUnit + `Microsoft.Extensions.TimeProvider.Testing`
