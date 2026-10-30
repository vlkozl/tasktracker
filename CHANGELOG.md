# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A PowerShell installer for the current user, `scripts/Install-TaskTracker.ps1`. One command (`irm … | iex`, see the README) downloads the latest release from GitHub, installs it to `%LOCALAPPDATA%\Programs\TaskTracker` and adds that folder to the user's PATH. Running it again updates TaskTracker in place. It needs no GitHub CLI, authentication or administrator rights. `-Version vX.Y.Z` installs a specific release.

## [1.6.0] - 2026-10-28

### Added

- The version number is shown when TaskTracker starts, above the task picker, and in the header of the status screen, for example **TaskTracker 1.6.0 — Wed, 28 Oct 2026**.

### Changed

- Ctrl+C prints today's summary before exiting, like `Q`. Before, it only saved and exited.
- The keys on the status screen are listed in a new order: **[N] new task   [S] switch task   [P] pause   [E] end task   [R] report   [Q] quit**. `--help` and the README list them in the same order.

## [1.5.0] - 2026-10-28

### Added

- `TaskTracker report [yyyy-MM-dd] --intervals` (or `-i`) lists every interval of the day after the summary: start, end, duration and task, in time order. With `--json` it changes nothing, since the JSON already holds every interval.

## [1.4.0] - 2026-10-26

### Changed

- The task picker (at start and on `S`) shows at most 10 tasks, the most recently used first, and never scrolls. Typing searches every task ever logged, not only the last 7 days, and shows the first 10 matches. A note says how many more there are. In a window too short for 10, it shows as many as fit. Without a cancel option (at start), `Esc` clears the search.

## [1.3.0] - 2026-10-26

### Added

- `E` ends the current task (running or paused) without closing the window. The status screen shows **No task running** and nothing is timed until `S` or `N` starts the next task.

## [1.2.1] - 2026-10-25

### Changed

- The standalone `TaskTracker.exe` is about 12 MB instead of 72 MB. The bundled .NET runtime is trimmed to what the app uses and compressed. Logs and `current.json` are read and written exactly as before.

## [1.2.0] - 2026-10-25

### Added

- `Esc` in the switch-task picker (`S`) cancels, like choosing **<- Cancel**, and the current task keeps running. `Esc` also cancels typing a new task name (`N`, or **+ New task...** in the picker), as an empty name already did.
- `TaskTracker report [yyyy-MM-dd] --json` prints the day's log as JSON, with nothing else on stdout, for scripts. It matches what the report shows: the date comes from the file name and the totals are recalculated. A day without a log prints an empty log in the same shape, and a broken log is an error on stderr with exit code 1.

### Changed

- The `R` report no longer stops tracking. It shows today's summary with the running task counted up to now, updated every minute, and marked as running; any key returns to the status screen.
- `TaskTracker report` works while the TaskTracker window is open. It shows the log as last saved (at most a minute behind) and notes that the window is open.
- A task keeps the spelling it was last logged with on any earlier day, however it is typed later. Before, each day kept the spelling first typed that day, so `TASK-1123` and `task-1123` could appear on different days. A new task keeps the spelling it is first typed with.

### Fixed

- A log file locked for longer than the retries (antivirus, sync tools, the search indexer) no longer stops the task picker from opening. The file is skipped and its tasks are left out of the list until it can be read.
- If saving fails when the first task starts, the app no longer crashes on startup. It keeps tracking and retries the save every minute, as it already did for later saves.

## [1.1.0] - 2026-10-24

### Added

- `TaskTracker report [yyyy-MM-dd]` prints a day's summary from the log and exits; without a date it reports today. Today's report refuses to run while TaskTracker is open, because that copy may still be timing a task. Other days can be reported at any time.
- `TaskTracker start <task>` and `TaskTracker end [<task>]` time a task without keeping the window open. Starting another task switches to it. On `end`, time the PC was asleep, hibernating or switched off is read from the Windows event log and left out, and the time is split at midnight if needed. The running task is kept in `current.json` next to the logs folder. Opening the app carries on with it, and the report shows it as running. A single run longer than 7 days, or a start time later than the clock, is refused rather than recorded.
- `TaskTracker --help` (also `-h`, `-?`, `/?`) describes the app, its commands and keys, and where logs are stored.
- `AGENTS.md` with build, test and architecture notes for AI coding agents working on the repository.
- MIT license.

### Changed

- The `T` (today) key is replaced by `R` (report). The report stops tracking first, so no task is timed while it is on screen, and shows today's summary as saved in the log. From the report, press `I` for all intervals, `Q` to quit, or any other key to pick a task and carry on.
- The summary table is titled with its date (for example "Saturday, 24 Oct 2026") instead of "Today", since reports can show other days.
- The exe's author and copyright details now show "Vladimir Kozlov".
- A new signing certificate is named `CN=Vladimir Kozlov (TaskTracker code signing)`. Certificates created with the old default name are no longer found by `Sign-Exe.ps1`, so create a new one before signing.

### Removed

- The public signing certificate is no longer in the repository. `New-SigningCert.ps1` exports it to the git-ignored `signing/` folder; copy it to other PCs yourself.

## [1.0.0] - 2026-10-24

### Added

- Status screen showing the current task's time today and the day's total (hours:minutes, updated every minute).
- On start, pick a recent task or type a new one.
- Keys to pause or resume (`P`), switch task (`S`), start a new task (`N`), show today's summary (`T`) and quit (`Q`).
- Task picker with search, listing tasks from the last 7 days with the most recently used first.
- Task names are free text, matched case-insensitively and ignoring surrounding spaces.
- Today's summary with each task's interval count, first start, last end and total time, plus the grand total and number of tasks. Press `I` in the summary to list every interval. The summary is also shown on start, if today already has tasks, and when you quit.
- One JSON log per day in `%USERPROFILE%\.TaskTracker\logs\yyyy-MM-dd.json`, grouped by task, with the start, end and duration of each interval.
- Every start, pause, resume and switch is saved immediately. The running interval is also saved every minute, on quit, on `Ctrl+C` and when the console window is closed, so at most about one minute is lost if the app is killed.
- Logs are written to a temporary file and then swapped in, so a crash never leaves a half-written log.
- Time while the PC is asleep or hibernating for more than 5 minutes is not counted.
- Setting the system clock back never produces negative durations. Small corrections of up to 5 seconds are ignored.
- Intervals that run past midnight are split between the two days' log files.
- Saving retries briefly when antivirus or sync tools lock the log. If a save still fails, tracking continues and the save is retried on the next minute. On quit, a failed save is retried once before the app exits.
- If today's log file is unreadable or corrupt, the app shows the error and exits without changing the file.
- Only one copy of the app can run at a time for each Windows user. A second copy shows a message and exits.
- Standalone 64-bit Windows single-file `TaskTracker.exe` built with the **publish (single exe)** VS Code task. It doesn't need .NET installed, and it has product details and an icon.
- Optional self-signed code signing with the **publish + sign** task, plus scripts to create, use and trust the certificate. Signatures are timestamped, so signing needs internet access. Signing is not required to distribute the exe.
- VS Code build, test and publish tasks, an F5 debug configuration and an xUnit test suite.

[unreleased]: https://github.com/vlkozl/tasktracker/compare/v1.6.0...HEAD
[1.6.0]: https://github.com/vlkozl/tasktracker/compare/v1.5.0...v1.6.0
[1.5.0]: https://github.com/vlkozl/tasktracker/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/vlkozl/tasktracker/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/vlkozl/tasktracker/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/vlkozl/tasktracker/compare/v1.2.0...v1.2.1
[1.2.0]: https://github.com/vlkozl/tasktracker/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/vlkozl/tasktracker/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/vlkozl/tasktracker/releases/tag/v1.0.0
