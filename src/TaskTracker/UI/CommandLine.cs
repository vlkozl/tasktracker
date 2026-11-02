using System.Globalization;
using Spectre.Console;
using TaskTracker.Models;
using TaskTracker.Reporting;
using TaskTracker.Storage;
using TaskTracker.Tracking;

namespace TaskTracker.UI;

public sealed record ReportArgs(DateOnly? Day, bool Json, bool Intervals);

/// <summary>The one-shot commands: <c>start</c>, <c>end</c>, <c>report</c> and <c>--help</c>. Each prints and exits.</summary>
public static class CommandLine
{
    private const string Usage =
        "Usage: [bold]TaskTracker[/] [[start <task> | end [[<task>]] | report [[yyyy-MM-dd]] [[--intervals]] [[--json]]]]   (see [bold]TaskTracker --help[/])";

    private const string AppIsOpen =
        "[red]TaskTracker is open.[/] Switch or stop tasks in that window, or quit it first.";

    /// <summary>How often the open app saves its running interval (its heartbeat), plus some slack.</summary>
    private static readonly TimeSpan AppSaveInterval = TimeSpan.FromMinutes(2);

    public static int Run(string[] args, DayLogStore logs, CommandTracker commands, TimeProvider time)
    {
        // Task names may be typed without quotes: "TaskTracker start Fix login bug".
        var rest = string.Join(' ', args.Skip(1)).Trim();
        var now = LocalClock.Now(time);
        var today = LocalClock.DateOf(now);

        switch (args[0].ToLowerInvariant())
        {
            case "--help" or "-h" or "-?" or "/?" or "help" when rest.Length == 0:
                ConsoleUi.WriteHelp(logs.Directory);
                return 0;

            // Only a new name is checked: a longer one already logged (before the limit) keeps working.
            case "start" when DayLog.IsTooLong(rest) && logs.KnownSpelling(rest, before: today.AddDays(1)) is null:
                AnsiConsole.MarkupLine($"[red]A task name has at most {DayLog.MaxNameLength} characters.[/] Nothing was started.");
                return 1;

            case "start" when rest.Length > 0:
                return Exclusive(AppIsOpen, () => Start(commands, rest, today));

            case "end":
                return Exclusive(AppIsOpen, () => End(commands, rest.Length == 0 ? null : rest, today));

            // Reports only read the logs, so they run even while the app is open and timing a task.
            case "report" when ParseReportArgs(args.Skip(1)) is { } report:
                return report.Json
                    ? WriteJson(logs, report.Day ?? today)
                    : Guarded(() => Report(logs, commands, report.Day ?? today, report.Intervals, now));

            default:
                AnsiConsole.MarkupLine(Usage);
                return 1;
        }
    }

    private static int Start(CommandTracker commands, string task, DateOnly today)
    {
        var result = commands.Start(task);
        var name = Markup.Escape(result.Open.Task);
        if (result.AlreadyRunning)
        {
            AnsiConsole.MarkupLine($"[green]► {name} is already running[/] (since {Time(result.Open.Start, today)}).");
            return 0;
        }

        if (result.Ended is not null)
            WriteEnded(result.Ended);
        AnsiConsole.MarkupLine($"[green]► Started {name}[/] at {result.Open.Start:HH:mm}. End it with [bold]TaskTracker end[/].");
        return 0;
    }

    private static int End(CommandTracker commands, string? task, DateOnly today)
    {
        var current = commands.Current;
        if (current is null)
        {
            AnsiConsole.MarkupLine("[yellow]No task is running.[/] Start one with [bold]TaskTracker start <task>[/].");
            return 1;
        }
        if (task is not null && !current.Is(task))
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(DayLog.NormalizeName(task))} is not running.[/] " +
                $"{Markup.Escape(current.Task)} is, since {Time(current.Start, today)}.");
            return 1;
        }

        WriteEnded(commands.End()!);
        return 0;
    }

    private static int Report(DayLogStore logs, CommandTracker commands, DateOnly day, bool intervals, DateTimeOffset now)
    {
        var today = LocalClock.DateOf(now);
        var log = logs.Load(day);
        var summary = DaySummary.From(log);
        ConsoleUi.WriteReport(summary, intervals: intervals);

        // The running task's time is written only when it ends, so it is not in the table yet. If the
        // table already has time for it since its start, an end is being written right now: say nothing,
        // or that time would look as if it should be counted twice.
        if (commands.Current is { } open && LocalClock.DateOf(open.Start) <= day && day <= today &&
            log.FindTask(open.Task)?.Intervals.Any(i => i.Start >= open.Start) != true)
        {
            AnsiConsole.MarkupLine(
                $"[green]► {Markup.Escape(open.Task)} is running since {Time(open.Start, day)}[/][grey], not included above. " +
                "End it with TaskTracker end.[/]");
        }

        // The app saves its running interval every minute, but this copy can't tell which task that is.
        // Just after midnight, the app may not have written the end of yesterday's interval yet.
        var appMayBeBehind = day == today ||
            (day == today.AddDays(-1) && now.TimeOfDay < AppSaveInterval);
        if (appMayBeBehind && SingleInstance.IsHeld())
        {
            AnsiConsole.MarkupLine(
                "[yellow]! TaskTracker is open and may be timing a task.[/] " +
                "[grey]Its time is included up to the last save (at most a minute ago). Press R there for a live report.[/]");
        }
        if (File.Exists(logs.PathFor(day)))
            AnsiConsole.MarkupLine($"[grey]Log: {Markup.Escape(logs.PathFor(day))}[/]");
        return 0;
    }

    /// <summary>
    /// An optional date, <c>--intervals</c> (<c>-i</c>) and <c>--json</c>, in any order; null if anything else is
    /// given. With <c>--json</c>, <c>--intervals</c> changes nothing: the JSON already holds every interval.
    /// </summary>
    public static ReportArgs? ParseReportArgs(IEnumerable<string> args)
    {
        DateOnly? day = null;
        bool json = false, intervals = false;
        foreach (var arg in args)
        {
            if (!json && string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                json = true;
            else if (!intervals && (string.Equals(arg, "--intervals", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(arg, "-i", StringComparison.OrdinalIgnoreCase)))
                intervals = true;
            else if (day is null && DateOnly.TryParseExact(arg, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                day = date;
            else
                return null;
        }
        return new ReportArgs(day, json, intervals);
    }

    /// <summary>
    /// The day's log as JSON, for scripts: nothing else goes to stdout (no notes about a running
    /// task), and errors go to stderr so they can't be mistaken for JSON.
    /// </summary>
    private static int WriteJson(DayLogStore logs, DateOnly day)
    {
        string json;
        try
        {
            json = logs.ReadJson(day);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        Console.Out.Write(json);
        return 0;
    }

    private static void WriteEnded(EndedTask ended)
    {
        AnsiConsole.MarkupLine(
            $"[grey]■[/] Ended [bold]{Markup.Escape(ended.Task)}[/]: [bold]{ConsoleUi.FormatDuration(ended.Worked)}[/] " +
            $"({Time(ended.Start, ended.End)}-{ended.End:HH:mm})");
        if (ended.OffUnknown)
            AnsiConsole.MarkupLine("[yellow]  Could not read the Windows sleep history, so time asleep or switched off may be counted.[/]");
        else if (ended.Off >= TimeSpan.FromMinutes(1))
            AnsiConsole.MarkupLine($"[grey]  {ConsoleUi.FormatDuration(ended.Off)} asleep or switched off, not counted.[/]");
    }

    /// <summary>Runs <paramref name="action"/> only while no other copy is running; the logs are then ours to write.</summary>
    private static int Exclusive(string refusal, Func<int> action)
    {
        using var singleInstance = SingleInstance.TryAcquire();
        if (singleInstance is null)
        {
            AnsiConsole.MarkupLine(refusal);
            return 1;
        }
        return Guarded(action);
    }

    private static int Guarded(Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    /// <summary>HH:mm, with the date in front when it isn't the same day as <paramref name="reference"/>.</summary>
    private static string Time(DateTimeOffset time, DateTimeOffset reference) =>
        Time(time, LocalClock.DateOf(reference));

    private static string Time(DateTimeOffset time, DateOnly reference) =>
        LocalClock.DateOf(time) == reference ? $"{time:HH:mm}" : $"{time:dd MMM HH:mm}";
}
