# FocusFlow

A minimalist desktop utility for managing study/work sessions and breaks, designed to
integrate seamlessly with macOS and Windows.

FocusFlow lives in the macOS menu bar / Windows system tray. Closing *or* minimising the
window hides it to the tray and keeps the timer running; the app only quits from the tray
menu.

---

## Status

The timer, persistence, session history, the tray surface and macOS packaging are done and
covered by tests. Windows packaging, global hotkeys, logging and localization are not
started — see [Not implemented](#not-implemented).

| | |
|---|---|
| Tests | 84 passing (xUnit + `FakeTimeProvider`) |
| Builds | Debug + Release, both target frameworks, 0 warnings |
| macOS | `.app` + DMG build and run; verified menu-bar only |
| Windows | Compiles and publishes — **the runtime path has never been executed** |

---

## Features

**Sessions**
- Start / stop / reset, plus **Skip** to advance manually
- Study 1–120 min (default 25), break 1–60 min (default 5)
- Standalone break, without running a study session first
- **Infinite mode**, or a finite run of 1–10 sessions
- Auto-start the next break and/or study session, configurable independently

**Focus discipline**

There is deliberately **no Pause during a study session**. Starting one is a commitment,
and an easy Pause is what erodes it. Stop remains the way out and is recorded in the
history as an abandoned session. Breaks stay pausable — pausing a break isn't a lapse.

**Alerts**
- A configurable **reminder before a session ends** (1–10 minutes), which stands in for
  watching the clock
- Native notifications — Action Center toasts on Windows, banners on macOS
- Alarm sound with volume control, from built-in system sounds or your own WAV/MP3
- Optional music when a break ends
- A warning window if the machine slept through the end of a session

**Persistence** — all local, no network, no account
- Settings survive restarts and are versioned for a future import/export
- An interrupted session is restored on the next launch, always paused
- Every finished session is appended to a history log for later reporting
- If any of that fails, **you are told** rather than left with silently missing data

**Platform**
- Menu bar shows the remaining minutes; system tray shows it on hover
- Launch at login (per-user; no administrator rights)
- Light / dark / follow-system theme
- Survives sleep, wake and system clock changes without losing time
- Only one instance runs; a second launch surfaces the first
- HiDPI aware; the window reopens on whichever monitor you're using

---

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone <repo-url> && cd focus-flow
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

### Packaging (macOS)

```bash
./build/macos/package.sh arm64
```

Accepts `arm64`, `x64` or `universal`, and writes `dist/macos/FocusFlow.app` plus a DMG.
Only `arm64` has been exercised — the `lipo` merge in `universal` is unproven.

The result is **ad-hoc signed**, which runs on the build machine but is blocked by
Gatekeeper anywhere else. Real distribution needs an Apple Developer ID:

```bash
CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" ./build/macos/package.sh universal
```

The script switches on the hardened runtime when a real identity is present. Notarisation
(`xcrun notarytool`) is still a separate step and is not scripted.

> **Note:** launch-at-login is only offered from a packaged `FocusFlow.app`. The agent has
> to launch the bundle rather than the executable inside it, so there is nothing valid to
> register under `dotnet run`. The app detects this and says so.

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
  regardless of whether the platform's monotonic clock freezes during sleep. Sleeping is
  not focus time, so a session slept through is held, not completed.

`TimeProvider` is injected, so tests drive the clock deterministically instead of sleeping.
State is handed out as immutable snapshots, since the engine ticks on a timer thread while
the UI reads on the dispatcher thread.

---

## Data files

`%APPDATA%\FocusFlow\` on Windows, `~/Library/Application Support/FocusFlow/` on macOS.
Nothing leaves the machine.

| File | Purpose |
|---|---|
| `config.json` | Settings. Versioned, indented, safe to hand-edit. |
| `session.json` | The in-flight session, for crash recovery. Deleted on a clean stop. |
| `history.jsonl` | Append-only log of finished sessions. |
| `instance.lock` | Held by the running instance; a stale one does not block startup. |

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
| Tray surface | Native menu: readout + all actions | Same |
| Countdown | Rendered into the icon as `25m` | Tooltip on hover |
| Notifications | `osascript` | `ToastNotificationManagerCompat` |
| Audio | `afplay -v` | MCI (`mciSendString`, winmm) |
| Launch at login | `~/Library/LaunchAgents` → `open -a` the bundle | `HKCU\…\CurrentVersion\Run` |
| Pointer position | CoreGraphics `CGEventGetLocation` | `GetCursorPos` |
| Sleep detection | Poll-gap heuristic (shared) | Poll-gap heuristic (shared) |

Deliberate deviations from the obvious choices:

- **Windows audio uses MCI, not `System.Media.SoundPlayer`.** SoundPlayer is WAV-only with
  no volume control, so it cannot satisfy the MP3 + volume requirement. MCI's `mpegvideo`
  device handles both and ships with Windows, avoiding a dependency on NAudio or LibVLC.
  Windows *system-sound aliases* still go through `PlaySound` at system volume — the
  slider only affects file-based sounds.
- **The tray is Avalonia's `TrayIcon`, not WinForms `NotifyIcon`.** One implementation for
  both platforms, and no WinForms message pump inside an Avalonia app.
- **Everything lives on the tray menu, not a popup window.** Two attempts at a window on
  click both failed on macOS: with a menu attached the click goes to the menu, and with no
  menu attached Avalonia never raises `Clicked` at all, leaving the icon inert. Avalonia
  offers no way to get a window from a status-item click.
- **The menu bar countdown is drawn into the icon bitmap.** There is no Avalonia API for
  text beside a status item, but the icon is just an image. It is rendered black on
  transparent and flagged `IsTemplateIcon` so macOS inverts it for a light or dark bar.

### Icons

| File | Used for |
|---|---|
| `Assets/app-icon.png` | window, Dock, Alt-Tab, Windows tray |
| `Assets/app-icon.ico` | Windows executable |
| `Assets/app-icon.icns` | macOS bundle |
| `Assets/tray-template.png` | macOS menu bar (idle) |

`tray-template.png` is a separate monochrome cut. A macOS template image is an alpha mask —
the colours are discarded and everything opaque is painted in the bar's tint — so the colour
logo collapses into one featureless silhouette. The template knocks the interior out to
negative space so the mark still reads.

---

## Not implemented

- **Global hotkeys** — needs `RegisterHotKey` on Windows and an Accessibility-permission
  monitor on macOS.
- **Windows packaging** — no MSIX manifest, Winget manifest or installer.
- **Code signing / notarisation** on either platform.
- **Launch minimized** — the window is shown on first launch.
- Logging, localization, accessibility (screen reader / high contrast), auto-update.
- No CI pipeline.
- Website blocking and cloud sync are **out of scope** for this version.

---

## Caveats

- **The Windows runtime path has never been executed.** Everything compiles and publishes,
  but MCI audio, the Run key, toasts, `GetCursorPos` and the DPI manifest were only built
  from macOS. Smoke-test these first on a real Windows machine.
- **macOS notifications are attributed to "Script Editor"**, not FocusFlow, because they go
  through `osascript`. Fixing that needs `UNUserNotifications` interop.
- MCI's `mpegvideo` device is missing on Windows N editions without the Media Feature Pack;
  MP3 falls back to the alias beep there.
- On a **mixed Retina / non-Retina** macOS setup, the active-monitor calculation can pick a
  neighbouring display, so the window opens on the wrong monitor. Correct when all displays
  share a scale factor.
- The settings window is ~880 px tall with no scrollbar, so it **will not fit a 768p
  laptop**.
- Performance targets (memory, CPU, startup) have not been measured.

---

## Tech stack

C# / .NET 10 · Avalonia 12.1 · CommunityToolkit.Mvvm · Microsoft.Extensions.DependencyInjection ·
System.Text.Json · xUnit + `Microsoft.Extensions.TimeProvider.Testing`
