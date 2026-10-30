# AGENTS.md

Instructions for AI coding agents working on this repository. For what the app does, its keys,
log format and code signing, read [README.md](README.md). This file covers how to change the code safely.

## Design principle: stay simple, extend on top

The main promise of the app is that it is simple to use, and it must stay that way. New features are added
on top of what users already know (*progressive disclosure*), and they never change how the existing,
simple behaviour works (*backward compatibility*). This covers everything a user sees or keeps: keys, commands,
flags, screens, reports, output, and the files the app creates.

- **The basic use doesn't change.** `TaskTracker`, `start <task>`, `end` and `report` with no extra arguments,
  and the existing keys, behave as they do now. Don't add a required argument, step, prompt or setting to them.
- **New behaviour is opt-in.** Add it as an optional flag (like `report --intervals`), a new key or a new
  command, so that a user who doesn't use it never notices it. Combinations that make no sense are harmless
  (`--intervals` with `--json` is ignored), not errors.
- **Additive only.** Don't remove or rename keys, commands or flags, and don't give an existing one a new
  meaning. Existing output keeps its shape. `report --json` keeps nothing but JSON on stdout.
- **Files.** No new files or folders unless a feature can't work without them, and no config file that must
  exist. The log format stays backward compatible (see [Storage](#storage)).
- **Small surface.** Prefer extending an existing command or screen over adding a new one, and good defaults
  over options. Show a key only when it can do something (idle hides `P` and `E`).
- **Everything visible is documented** in `--help`, [README.md](README.md) and `CHANGELOG.md`.
- If a change can't follow these rules, it is a breaking change: say so and ask the user before making it.

## Project overview

- Windows-only console time tracker: C# / .NET 10 (`net10.0`), UI built with Spectre.Console.
  It relies on Windows behaviour: console window close events, `%USERPROFILE%` paths, and a named mutex.
- Solution: `TaskTracker.slnx` (app: `src/TaskTracker`, tests: `tests/TaskTracker.Tests`).
- Layout of `src/TaskTracker`:
  - `Program.cs`: hands arguments to `UI/CommandLine.cs`; otherwise takes the single-instance mutex, picks up a
    task left open by `start`, runs the 1-minute heartbeat timer and Ctrl+C/SIGHUP handlers, and starts the UI.
  - `Tracking/Tracker.cs`: all time and state logic of the interactive app.
  - `Tracking/CommandTracker.cs`: `start`/`end` without a running process (see below).
  - `Tracking/PowerHistory.cs`: sleep, shutdown and Modern Standby periods from the Windows System event log.
  - `Tracking/LocalClock.cs`, `Tracking/SingleInstance.cs`: shared time helpers and the mutex.
  - `Storage/DayLogStore.cs`: reads and writes the per-day JSON files. `Storage/OpenTaskStore.cs`: `current.json`.
  - `Models/`: `DayLog`, `TaskLog`, `Interval`, which are the JSON shape, plus `OpenTask`.
  - `Reporting/DaySummary.cs`: immutable snapshot for display.
  - `UI/ConsoleUi.cs`: Spectre screens and prompts. `UI/CommandLine.cs`: `start`, `end`, `report`, `--help`.

## Commands

```powershell
dotnet build
dotnet test
dotnet run --project src/TaskTracker
dotnet publish src/TaskTracker/TaskTracker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

VS Code tasks in `.vscode/tasks.json` wrap the same commands (`build`, `test`, `publish (single exe)`, `publish + sign`).

- Run the interactive app only in a real terminal. It reads single key presses (`Console.ReadKey`/`KeyAvailable`),
  so it can't be driven with redirected stdin. That is also why `.vscode/launch.json` uses `integratedTerminal`.
  Verify behaviour with tests, not by scripting the UI.
- If the user has TaskTracker running, it locks `bin/Debug` and `dotnet build`/`dotnet test` fail with
  MSB3027 (file locked). Don't kill the user's process. Build into another folder instead:
  `dotnet test --artifacts-path <temp dir>`.
- A second instance refuses to start (mutex `Global\TaskTracker-<UserName>`). This is by design.

## Architecture and invariants

- `Tracker` is the only owner of time and state. It is called from the UI thread, the heartbeat timer thread
  and the Ctrl+C/SIGHUP handlers, so every public method takes `_lock` (a `System.Threading.Lock`). Keep it that way.
- Mutate in memory first, then save. `_dirty` marks unsaved state. If a save throws, state stays dirty and the
  next `Tick` writes it.
- `CatchUp()` runs at the start of every operation and handles three cases:
  - a sleep gap (more than `MaxGap`, 5 min);
  - the clock being set back (backsteps of up to 5 s are absorbed);
  - midnight rollover, which splits the open interval at local midnight.
  The only save inside `CatchUp` is the midnight rollover, because the old day must be written before it is
  replaced. It saves only when dirty, so a paused or idle tracker never writes empty day files.
- Zero-length intervals are dropped in `CloseOpen`, never stored.
- `start`/`end` keep the running task in `current.json` and write its interval to the day logs only on `end`
  (or on `start` of another task, or when the app opens and carries on with it). So the log format never holds an
  unfinished interval. With no heartbeat, off time comes from `IPowerHistory` instead of `MaxGap`. Recording is
  idempotent: the task's intervals inside start..end are replaced, never added to, so a crash before
  `current.json` is deleted can't double-count. Recording refuses (and keeps `current.json`) when the clock is
  more than 5 s behind the start or the interval is longer than `CommandTracker.MaxOpen` (7 days). Errors about
  `current.json` come from `OpenTaskStore.Broken` and tell the user to fix or delete the file.
- Only one writer at a time: the app, `start` and `end` hold the single-instance mutex. `report` only reads, so it
  runs without it. To say whether the app is open it only looks (`SingleInstance.IsHeld`, `Mutex.TryOpenExisting`):
  taking the mutex, even briefly, would make the app or a command refuse to start. The `R` report never stops tracking.
- `DayLogStore.NameFor` decides the spelling a task is logged under; use it from every entry point. The spelling
  lookup reads each older day file once per process and keeps the names, because only the running copy writes
  and it writes today's file. Reading old files for names skips broken and locked files, so they never block a start.
- Inject `TimeProvider` and get the time through `LocalClock.Now(TimeProvider)`. Never call `DateTime.Now` directly.
  Timestamps are truncated to whole seconds.
- The heartbeat callback in `Program.cs` catches only `IOException`, `UnauthorizedAccessException` and
  `InvalidDataException`. Any other exception on the timer thread kills the process, so code reached from
  `Tick` must not throw anything else.

## Storage

- One file per day: `%USERPROFILE%\.TaskTracker\logs\yyyy-MM-dd.json`. The JSON is camelCase
  (`JsonSerializerDefaults.Web`) and grouped by task: `tasks[].intervals[]` with `start`/`end`/`durationSeconds`,
  plus a `totalSeconds` for each task. `DayLog.RecalculateTotals()` fills in the redundant totals on save.
- All JSON goes through the source-generated `LogJson` context (`Storage/LogJson.cs`), never the reflection-based
  `JsonSerializer` overloads. The published exe is trimmed (`PublishTrimmed`), and reflection could lose members
  at run time. A new type that is written to disk needs its own `[JsonSerializable]` line. After changing
  anything about publishing, publish and check that there are no `IL2026`/`IL3050` trim warnings.
- Writes are atomic: write `<file>.tmp`, then `File.Move(..., overwrite: true)`.
- Transient locks (antivirus, sync tools, the search indexer) surface as `IOException` or
  `UnauthorizedAccessException`, so always catch both. `Load` and `Save` retry up to 4 attempts, 75 ms apart.
- The file name is the source of truth for the date. `Load` overwrites `date` and normalizes null lists.
  Invalid JSON becomes `InvalidDataException`.
- Users have existing logs. Changing the JSON shape is a breaking change, so keep it backward compatible
  (old files must still load).

## UI (Spectre.Console)

- `AnsiConsole.Live` draws nothing until `ctx.Refresh()`. Keep the initial `Refresh()` in `WaitForAction`.
- Task names are arbitrary user text. Wrap them in `Markup.Escape` everywhere they appear in markup,
  including `SelectionPrompt.UseConverter`.
- Prompts can't run inside `Live`. The main loop leaves `Live` when a key is pressed, and only then prompts.

## Testing

- xUnit 2 with `Microsoft.Extensions.TimeProvider.Testing`. Add a test for every behaviour change in `Tracker`
  or `DayLogStore`.
- Make time deterministic with `FakeTimeProvider` and `SetLocalTimeZone(TimeZoneInfo.Utc)`.
- `FakeTimeProvider` can't move backwards. For clock-set-back scenarios, use the `ManualClock` helper in `TrackerTests`.
- Simulate the running app with the `Work` helper, which advances 1 minute and calls `Tick` for each minute.
  A single jump of more than 5 minutes triggers the sleep-gap logic.
- Use `TempStore` (a throwaway log directory under `%TEMP%`) and never the real `DayLogStore.DefaultDirectory`.

## Code style

- Match the existing code: file-scoped namespaces, nullable enabled, implicit usings, primary constructors
  where they are already used, the `Lock` type for locks.
- Write short XML doc comments only where they explain *why*.
- Don't add dependencies without a clear reason.

## Scripts

- `scripts/*.ps1` must also run in Windows PowerShell 5.1:
  - no ternary operator and no `??`;
  - `$PSScriptRoot` is empty inside `param()` defaults in 5.1, so resolve defaults in the script body.
- `Install-TaskTracker.ps1` covers the main path in Windows PowerShell 5.1 and PowerShell 7, and stays short.
  Don't add handling for rare cases such as antivirus locks, two installers at once, 32-bit hosts or locked-down
  sessions. It also runs as `irm … | iex`, in the user's own session:
  - `param()` is then an ordinary variable declaration in the user's session: no validation attributes (check in
    the body), and a name the user won't have (`$TaskTrackerVersion`, alias `-Version`), removed at the start;
  - keep everything after `param()` inside `& { … }` so settings and variables don't stay behind;
  - change the user PATH only through `HKCU\Environment` with `DoNotExpandEnvironmentNames`, never with
    `[Environment]::SetEnvironmentVariable('Path', …)`, which expands `%VAR%` entries and writes `REG_SZ`;
  - pass `[System.Management.Automation.Language.NullString]::Value`, not `$null`, to .NET `string` parameters:
    PowerShell passes `$null` as `""`, and `SetEnvironmentVariable` in PowerShell 7 stores that instead of deleting;
  - a running exe can be renamed, so `File.Replace` succeeds under a running TaskTracker. The process check
    before the download is what prevents that.
- Test the installer on a copy with the install folder and the registry key redirected, never against the real profile.
- Code signing is optional. It uses a self-signed certificate, and `Sign-Exe.ps1` fails if it can't get a timestamp.
  The certificate subject (`CN=Vladimir Kozlov (TaskTracker code signing)`) is a default in both
  `New-SigningCert.ps1` and `Sign-Exe.ps1`. If you change it, change it in both.
- Never commit certificates or keys. `*.pfx`, `*.p12`, `*.snk` and the whole `signing/` folder (where the
  public `.cer` is exported) are gitignored.

## Changelog, versioning, git

- `CHANGELOG.md` follows Keep a Changelog 1.1.0 and SemVer. Add user-facing changes under `## [Unreleased]`,
  using only the Added / Changed / Deprecated / Removed / Fixed / Security headings.
  When releasing, update the link references at the bottom.
- The version is set in `<Version>` in `src/TaskTracker/TaskTracker.csproj`. Releases are tagged `vX.Y.Z`.
- Only `.vscode/tasks.json` and `launch.json` are committed. `bin/`, `obj/`, `publish/`, `signing/`, `.vs/`,
  `.serena/` and `.remember/` are ignored, so don't commit them.
- The repo is used on Windows with `core.autocrlf=true`. LF-to-CRLF warnings from git are harmless.
