# TaskTracker

A 100% vibe coded Windows console app that tracks how much time you spend on tasks.
Type a task name, and a timer starts. You can switch between tasks, pause, and see
today's totals at any time.

It is meant to stay that simple. New features are optional extras on top (a flag, a key, a command), and
what already works keeps working the same way: the same keys, commands, reports and log files.

## Run

```powershell
dotnet run --project src/TaskTracker
```

In VS Code, press **F5**. The app runs in the integrated terminal, because it needs key presses.

To build a standalone `TaskTracker.exe`, run the **publish (single exe)** task. The exe is written to `publish/`.
It is about 12 MB (the .NET runtime is bundled, trimmed to what the app uses and compressed), so it doesn't need
.NET installed on the target PC. It doesn't need to be signed (see [Code signing](#code-signing-optional)).

## Install for the current user

Install or update from PowerShell:

```powershell
irm https://raw.githubusercontent.com/vlkozl/tasktracker/main/scripts/Install-TaskTracker.ps1 | iex
```

On an old Windows PowerShell setup where this fails with an SSL/TLS error, run
`[Net.ServicePointManager]::SecurityProtocol = 'Tls12'` first, then the command again. The setting lasts only for
that window, so the `powershell -ExecutionPolicy Bypass -File` commands below, which start a new one, don't get it.
Use `powershell -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; & .\Install-TaskTracker.ps1"` instead.

To install a specific release, use this form, which passes `-Version` to the script:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/vlkozl/tasktracker/main/scripts/Install-TaskTracker.ps1))) -Version v1.4.0
```

Prefer to read the script before running it? Download it, look at it, then run the file. Run this way,
`TaskTracker` is found in new terminals, not in the one you ran the installer from.

```powershell
irm https://raw.githubusercontent.com/vlkozl/tasktracker/main/scripts/Install-TaskTracker.ps1 -OutFile Install-TaskTracker.ps1
notepad Install-TaskTracker.ps1
powershell -ExecutionPolicy Bypass -File .\Install-TaskTracker.ps1
```

Or run the script from a local copy of this repository:

```powershell
.\scripts\Install-TaskTracker.ps1
# Or install a specific release:
.\scripts\Install-TaskTracker.ps1 -Version v1.4.0
```

If Windows PowerShell says running scripts is disabled, run it as
`powershell -ExecutionPolicy Bypass -File scripts\Install-TaskTracker.ps1` instead; `TaskTracker` is then found
in new terminals. The one-line install above isn't affected.

The script downloads the public `TaskTracker.exe` release asset directly. GitHub CLI and GitHub
authentication are not needed.

It downloads the latest stable release by default, installs to `%LOCALAPPDATA%\Programs\TaskTracker`,
and adds that folder to your user `PATH`. After the one-line install, `TaskTracker` runs in the same window
straight away; terminals that were already open need a restart to find it. No administrator rights or .NET installation are needed.
Rerun the script to update: the new version replaces the old one in the same folder. Close TaskTracker
first if it is running. Your logs and `current.json` stay in `%USERPROFILE%\.TaskTracker`.
The script supports Windows PowerShell 5.1 and PowerShell 7.

## Keys

| Key | Action |
|-----|--------|
| `N` | Type a new task name straight away. `Esc` (or an empty name) cancels |
| `S` | Switch task: pick one of the 10 latest tasks (type to search all tasks) or choose **+ New task...**. `Esc` cancels and changes nothing |
| `P` | Pause / resume the current task |
| `E` | End the current task and keep the window open. Nothing is timed until `S` or `N` starts the next task |
| `R` | Report: today's summary while tracking carries on, with the running task counted up to now and updated every minute. `I` lists every interval, `Q` quits, any other key returns |
| `Q` | Stop, save, and print today's summary. `Ctrl+C` does the same |

## Commands

You don't have to keep the window open. These commands do one thing and exit:

| Command | Action |
|---------|--------|
| `TaskTracker start <task>` | Start a task. If another one is running, it ends first (a switch) |
| `TaskTracker end [<task>]` | End the running task and print its time. With a name, it checks that this task is the one running |
| `TaskTracker report [yyyy-MM-dd]` | Print a day's summary; today without a date |
| `TaskTracker report [yyyy-MM-dd] --intervals` | Print the summary and then every interval of the day (also `-i`) |
| `TaskTracker report [yyyy-MM-dd] --json` | Print the day's log as JSON, for scripts |
| `TaskTracker --help` | Commands and keys (also `-h`, `-?`, `/?`) |

Quotes are optional: `TaskTracker start Fix login bug` works. The running task is kept in
`%USERPROFILE%\.TaskTracker\current.json`, and its time is written to the day log when it ends, split at midnight if needed.
Nothing runs in the background, so time the PC was asleep, hibernating or switched off is read from the Windows
System event log at the end and left out. If that log can't be read, `end` says so and counts the whole time.
A single run can't be longer than 7 days: `end` refuses and asks you to correct the start in `current.json` (or delete
the file to discard the task).

While the TaskTracker window is open, `start` and `end` are refused, so only one copy times tasks.
When you open the window, it carries on with the task started with `start`.

`TaskTracker report` prints today's summary, and `TaskTracker report 2026-10-23` any other day (`yyyy-MM-dd`).
Reports only read the logs, so they never stop a task and work at any time:
- a task started with `start` is shown as running and isn't in the table yet: its time is added when it ends;
- if the TaskTracker window is open, the report says so. Its current task is included up to the last save, at
  most a minute ago. Press `R` in the window for a live report.

Add `--intervals` (or `-i`, before or after the date) to list the day's logged intervals after the summary: start, end,
duration and task, in time order. Like the summary, it shows the log as saved, so a task started with `start` isn't
listed until it ends. With `--json` it changes nothing: the JSON already holds every interval.

Add `--json` (before or after the date) to get the day's log as JSON, with nothing else on stdout, so it can be
piped: `TaskTracker report --json | ConvertFrom-Json` or `TaskTracker report 2026-10-23 --json | jq .`.
It is the file as the report reads it: the date comes from the file name, missing lists are filled in and the totals
are recalculated, so it always matches `report`. A day without a file prints an empty log (`{"date": "…", "tasks": []}`).
The notes about a running task are left out, and errors, including a broken log file, go to stderr with exit code 1.
The format is described under [Logs](#logs).

Run `TaskTracker --help` (or `-h`, `-?`, `/?`) for a summary of commands and keys.

Task names are any text. Matching ignores case and surrounding spaces, so `Bug fix` and `bug fix ` count as the same task.
A task keeps the spelling it was last logged with, however you type it later: once `TASK-1123` is in a log,
starting `task-1123` records it as `TASK-1123`. A new task keeps the spelling you first type.
The task picker shows at most 10 tasks, the most recently used first. Typing searches every task ever logged and
shows the first 10 matches; type more to narrow them down.

## Logs

Logs go to one file per day: `%USERPROFILE%\.TaskTracker\logs\yyyy-MM-dd.json`

```json
{
  "date": "2026-10-24",
  "tasks": [
    {
      "task": "Fix login bug",
      "totalSeconds": 4627,
      "intervals": [
        { "start": "2026-10-24T09:00:05+02:00", "end": "2026-10-24T09:47:12+02:00", "durationSeconds": 2827 }
      ]
    }
  ]
}
```

- **Saving:** the running interval is saved as soon as it starts and again every minute. Ctrl+C
  (which also prints today's summary) and closing the window also save it. If the app is killed, you lose at most about one minute.
- **Sleep:** if the PC sleeps for more than 5 minutes while a task is running, the sleep time is not counted.
- **Midnight:** an interval that runs past 00:00 is split between the two days' files.
- **One instance:** a second copy of the app refuses to start, so two copies can't overwrite each other's log.

## Code signing (optional)

Signing is **not required**. The **publish (single exe)** task produces an unsigned
`TaskTracker.exe` that runs normally. A self-signed signature changes little for others:
Windows still shows "Unknown publisher" and SmartScreen still warns about downloaded files.
Its only uses are proving the exe hasn't been modified, and showing a verified publisher on
your own PCs if you trust the certificate there.

No certificate is included in the repository. To sign your own builds:

1. **Create a certificate** (once): run `scripts\New-SigningCert.ps1`. It creates a certificate
   named `CN=Vladimir Kozlov (TaskTracker code signing)`, valid for 3 years, in your personal certificate store.
   It also exports the public part to `signing\tasktracker-codesign.cer`. The `signing/` folder is
   git-ignored, and the private key never leaves the PC.
2. **Sign:** use the **publish + sign** task instead of **publish (single exe)**. It runs
   `scripts\Sign-Exe.ps1`, which adds a timestamp, so the signature stays valid after the certificate expires.
   This step needs internet access.
3. **Trust the certificate on a PC** (optional, once per PC, no admin needed): copy the `.cer` file into
   that PC's `signing\` folder, then run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\Trust-SigningCert.ps1
   ```

   Windows asks for confirmation. Click **Yes** only if the thumbprint in the dialog matches the one the script printed.

To check an exe: right-click it → **Properties → Digital Signatures**, or run
`Get-AuthenticodeSignature TaskTracker.exe`.

## Tests

```powershell
dotnet test
```

## License

[MIT](LICENSE)
