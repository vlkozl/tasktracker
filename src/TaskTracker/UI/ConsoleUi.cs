using System.Globalization;
using System.Reflection;
using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using TaskTracker.Reporting;
using TaskTracker.Storage;
using TaskTracker.Tracking;

namespace TaskTracker.UI;

public sealed class ConsoleUi(Tracker tracker, DayLogStore store)
{
    private const int RecentDays = 7;
    private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly ConsoleKey[] ActionKeys =
        [ConsoleKey.P, ConsoleKey.E, ConsoleKey.S, ConsoleKey.N, ConsoleKey.R, ConsoleKey.Q];

    /// <summary>The release number, such as 1.5.0, without the commit hash the SDK appends after a +.</summary>
    private static readonly string Version = typeof(ConsoleUi).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    /// <summary>Taken by every redraw and by <see cref="Quit"/>, which Ctrl+C runs on another thread.</summary>
    private readonly Lock _screen = new();
    private bool _quitting;

    /// <summary>
    /// Where <see cref="Quit"/> writes. While a Live display runs, every write to the shared console
    /// redraws it afterwards, so Ctrl+C would leave the status panel under the report. A console of
    /// its own isn't hooked by Live.
    /// </summary>
    private readonly IAnsiConsole _quitConsole = AnsiConsole.Create(new AnsiConsoleSettings());

    /// <param name="continueTask">A task started with <c>TaskTracker start</c> to keep timing, instead of asking.</param>
    public void Run(string? continueTask = null)
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine($"[bold]TaskTracker[/] {Markup.Escape(Version)}");
        var status = tracker.GetStatus();
        if (status.Day.TaskCount > 0)
            WriteReport(status.Day);

        var task = continueTask ?? PickTask(allowCancel: false)!;
        var running = true;
        CarryOnIfSaveFails(() => tracker.Start(task));
        while (running)
            CarryOnIfSaveFails(() => running = HandleNextAction());
    }

    private static void CarryOnIfSaveFails(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The tracker keeps its state and the heartbeat retries the save, so carry on.
            AnsiConsole.MarkupLine($"[red]Could not save the log:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Your change is kept and saving is retried every minute. Press any key.[/]");
            Console.ReadKey(intercept: true);
        }
    }

    /// <summary>Waits for one key action and performs it. Returns false when the user quits.</summary>
    private bool HandleNextAction()
    {
        Draw(AnsiConsole.Clear);
        switch (WaitForAction())
        {
            case ConsoleKey.P:
                if (tracker.State == TrackerState.Running)
                    tracker.Pause();
                else
                    tracker.Resume();
                break;

            case ConsoleKey.E:
                tracker.Stop();
                break;

            case ConsoleKey.S:
                StartIfChosen(PickTask(allowCancel: true));
                break;

            case ConsoleKey.N:
                StartIfChosen(AskNewTask(allowCancel: true));
                break;

            case ConsoleKey.R:
                return ShowReport();

            case ConsoleKey.Q:
                Quit();
                return false;
        }
        return true;
    }

    /// <summary>
    /// Stops and saves, retrying once on failure, then prints today's report. Used by Q and by Ctrl+C.
    /// The app exits afterwards whatever happens, since the heartbeat stops with it, so this never throws
    /// for a save or a broken log. Runs once: a second call (Ctrl+C after Q) waits for the first to
    /// finish, so the process can't end in the middle of a save.
    /// </summary>
    public void Quit()
    {
        lock (_screen)
        {
            if (_quitting)
                return;
            _quitting = true;

            var console = _quitConsole;
            console.Clear();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    tracker.Stop();
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 2)
                {
                    console.MarkupLine($"[yellow]Saving failed ({Markup.Escape(ex.Message)}), retrying...[/]");
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    console.MarkupLine($"[red]Could not save the last interval:[/] {Markup.Escape(ex.Message)}");
                    console.MarkupLine("[grey]At most the last minute is missing from the log.[/]");
                    break;
                }
            }

            try
            {
                WriteReport(tracker.GetStatus().Day, console);
                console.MarkupLine($"[grey]Log: {Markup.Escape(store.PathFor(tracker.Today))}[/]");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                console.MarkupLine($"[red]Could not show today's summary:[/] {Markup.Escape(ex.Message)}");
            }
            console.Cursor.Show(); // Live hides it, and Ctrl+C ends the process inside Live
        }
    }

    /// <summary>
    /// Redraws, unless <see cref="Quit"/> has run: then the final report is on screen, so this thread
    /// just waits for the process to end (Ctrl+C ends it once its handler returns).
    /// </summary>
    private void Draw(Action draw)
    {
        lock (_screen)
        {
            if (!_quitting)
            {
                draw();
                return;
            }
        }
        Thread.Sleep(Timeout.Infinite);
    }

    private void StartIfChosen(string? task)
    {
        if (task is not null)
            tracker.Start(task);
    }

    /// <summary>Shows the live status until an action key is pressed. Redraws only when the text changes (once a minute).</summary>
    private ConsoleKey WaitForAction()
    {
        var pressed = ConsoleKey.Q;
        var (text, view) = BuildStatusView(tracker.GetStatus());

        AnsiConsole.Live(view).Start(ctx =>
        {
            // Live draws nothing until the first refresh; without this the screen stays blank
            // until the status text first changes (up to a minute).
            Draw(ctx.Refresh);
            while (true)
            {
                while (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (IsAction(key))
                    {
                        // Leaving Live redraws the panel once more, which mustn't land on Ctrl+C's report.
                        Draw(() => pressed = key);
                        return;
                    }
                }

                var (newText, newView) = BuildStatusView(tracker.GetStatus());
                if (newText != text)
                {
                    text = newText;
                    Draw(() =>
                    {
                        ctx.UpdateTarget(newView);
                        ctx.Refresh();
                    });
                }
                Thread.Sleep(KeyPollInterval);
            }
        });
        return pressed;
    }

    /// <summary>P and E have nothing to act on while idle; ignoring them avoids a redraw that does nothing.</summary>
    private bool IsAction(ConsoleKey key) =>
        ActionKeys.Contains(key) &&
        !(tracker.State == TrackerState.Idle && key is ConsoleKey.P or ConsoleKey.E);

    private static (string Text, IRenderable View) BuildStatusView(TrackerStatus status)
    {
        var task = Markup.Escape(status.CurrentTask ?? "-");
        var duration = FormatDuration(status.CurrentTaskTotal);
        var current = status.State switch
        {
            TrackerState.Running => $"[green]▶ {task}[/]   [bold]{duration}[/]",
            TrackerState.Paused => $"[yellow]⏸ Paused: {task}[/]   [bold]{duration}[/]",
            _ => "[grey]■ No task running[/]",
        };
        // Two rows, read top to bottom per column: task choice, the current task, the day. Idle hides P and E.
        string[][] keys = status.State == TrackerState.Idle
            ? [["[N] new task", "[S] start a task"], ["[R] report", "[Q] quit"]]
            : [["[N] new task", "[S] switch task"],
               [status.State == TrackerState.Running ? "[P] pause" : "[P] resume", "[E] end task"],
               ["[R] report", "[Q] quit"]];

        var text =
            $"{current}\n" +
            $"Today: [bold]{FormatDuration(status.Day.Total)}[/] across [bold]{status.Day.TaskCount}[/] task(s)\n\n" +
            $"[grey]{KeyRow(keys, 0)}\n{KeyRow(keys, 1)}[/]";

        var panel = new Panel(new Markup(text))
            .Header($" TaskTracker {Markup.Escape(Version)} — {status.Day.Date:ddd, dd MMM yyyy} ")
            .Border(BoxBorder.Rounded)
            .Padding(2, 1);
        return (text, panel);
    }

    /// <summary>One row of the key columns, each column padded to its widest entry so the rows line up.</summary>
    private static string KeyRow(string[][] columns, int row) =>
        Markup.Escape(string.Concat(columns.Select((column, i) =>
            i == columns.Length - 1 ? column[row] : column[row].PadRight(column.Max(k => k.Length) + 3))));

    private string? PickTask(bool allowCancel)
    {
        var tasks = store.AllTaskNames(tracker.Today, RecentDays)
            .Where(t => !string.Equals(t, tracker.CurrentTask, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tasks.Count == 0)
            return AskNewTask(allowCancel);

        var (action, task) = TaskPicker.Pick(tasks, allowCancel);
        return action switch
        {
            PickAction.NewTask => AskNewTask(allowCancel),
            PickAction.Cancel => null,
            _ => task,
        };
    }

    private static string? AskNewTask(bool allowCancel)
    {
        if (!allowCancel)
            return AnsiConsole.Prompt(new TextPrompt<string>("Task name:")).Trim();

        AnsiConsole.Markup("Task name [grey](Esc or empty to cancel)[/]: ");
        var name = ReadLineOrEscape()?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Reads a line of text, returning null on Esc. Spectre's TextPrompt can't be cancelled with Esc,
    /// so this handles just the keys a task name needs: characters, Backspace, Enter and Esc.
    /// </summary>
    private static string? ReadLineOrEscape()
    {
        var start = Console.GetCursorPosition();
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    Console.WriteLine();
                    return null;

                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return text.ToString();

                case ConsoleKey.Backspace when text.Length > 0:
                    // a whole character, even one made of several chars (emoji, accents)
                    var erased = text.Length - StringInfo.ParseCombiningCharacters(text.ToString())[^1];
                    text.Length -= erased;
                    Redraw(text, start, erased);
                    break;

                default:
                    if (char.IsControl(key.KeyChar))
                        break;
                    text.Append(key.KeyChar);
                    if (!char.IsHighSurrogate(key.KeyChar)) // otherwise wait for the other half
                        Redraw(text, start, erased: 0);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes the text again from where it starts, instead of stepping back one cell per character:
    /// wide characters and the console's delayed wrap at the end of a line make cell counting unreliable.
    /// </summary>
    private static void Redraw(StringBuilder text, (int Left, int Top) start, int erased)
    {
        Console.SetCursorPosition(start.Left, start.Top);
        Console.Write(text.ToString());
        var (left, top) = Console.GetCursorPosition();
        Console.Write(new string(' ', 2 * erased)); // an erased char takes up to two cells
        Console.SetCursorPosition(left, top);
    }

    /// <summary>
    /// Shows today's report while tracking carries on. It is redrawn whenever what it shows changes
    /// (once a minute while a task runs), so the running task stays counted up to now.
    /// Returns false when the user quits from it.
    /// </summary>
    private bool ShowReport()
    {
        while (true)
        {
            var shown = "";
            DaySummary? shownDay = null;
            while (!Console.KeyAvailable)
            {
                var status = tracker.GetStatus();
                var line = ReportStatusLine(status);
                var text = line + string.Concat(status.Day.Tasks.Select(
                    t => $"\n{t.Task}|{t.IntervalCount}|{t.LastEnd:HH:mm}|{FormatDuration(t.Total)}"));
                if (text != shown)
                {
                    shown = text;
                    shownDay = status.Day;
                    Draw(() =>
                    {
                        AnsiConsole.Clear();
                        WriteReport(status.Day);
                        AnsiConsole.MarkupLine(line);
                        AnsiConsole.MarkupLine("[grey][[I]] all intervals   [[Q]] quit   any other key to return[/]");
                    });
                }
                Thread.Sleep(KeyPollInterval);
            }

            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.I:
                    AnsiConsole.Clear();
                    // The day on screen, not a newer snapshot, so the totals match the summary just shown.
                    WriteIntervals(shownDay ?? tracker.GetStatus().Day);
                    AnsiConsole.MarkupLine("[grey]Press any key to return to the report[/]");
                    Console.ReadKey(intercept: true);
                    break;

                case ConsoleKey.Q:
                    Quit();
                    return false;

                default:
                    return true;
            }
        }
    }

    private static string ReportStatusLine(TrackerStatus status)
    {
        var task = Markup.Escape(status.CurrentTask ?? "");
        return status.State switch
        {
            TrackerState.Running => $"[green]▶ {task} is running[/] [grey]({FormatDuration(status.CurrentTaskTotal)} today, counted up to now)[/]",
            TrackerState.Paused => $"[yellow]⏸ {task} is paused[/]",
            _ => "[grey]No task is running.[/]",
        };
    }

    /// <summary>Output of <c>TaskTracker --help</c>.</summary>
    public static void WriteHelp(string logDirectory)
    {
        AnsiConsole.MarkupLine($"[bold]TaskTracker[/] {Markup.Escape(Version)}");
        AnsiConsole.MarkupLine("Tracks how much time you spend on tasks. Pick or type a task and a timer starts;");
        AnsiConsole.MarkupLine("switch, pause and report while you work. Each day is saved as one JSON log.");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Usage[/]");
        var commands = new Grid().AddColumn(new GridColumn().PadLeft(2).PadRight(4)).AddColumn();
        commands.AddRow("TaskTracker", "Start tracking (asks for a task first)");
        commands.AddRow("TaskTracker start <task>", "Start a task without keeping a window open");
        commands.AddRow("TaskTracker end [[<task>]]", "End the task started with [bold]start[/]");
        commands.AddRow("TaskTracker report", "Print today's summary and exit");
        commands.AddRow("TaskTracker report [[yyyy-MM-dd]]", "Print another day's summary");
        commands.AddRow("TaskTracker report ... --intervals", "Also list every interval (also [bold]-i[/])");
        commands.AddRow("TaskTracker report ... --json", "Print the day's log as JSON, for scripts");
        commands.AddRow("TaskTracker --help", "Show this help (also [bold]-h[/], [bold]-?[/], [bold]/?[/])");
        AnsiConsole.Write(commands);
        AnsiConsole.MarkupLine("[grey]  start on another task switches to it. Quotes around task names are optional.[/]");
        AnsiConsole.MarkupLine("[grey]  Time the PC was asleep or off is read from Windows and not counted.[/]");
        AnsiConsole.MarkupLine("[grey]  While the TaskTracker window is open, use it instead: start and end are[/]");
        AnsiConsole.MarkupLine("[grey]  refused, so that only one copy times tasks. Opening it carries on[/]");
        AnsiConsole.MarkupLine("[grey]  with the task started with start.[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[bold]Keys while tracking[/]");
        var keys = new Grid().AddColumn(new GridColumn().PadLeft(2).PadRight(4)).AddColumn();
        keys.AddRow("N", "Type a new task name (Esc or empty cancels)");
        keys.AddRow("S", "Switch task: pick one of the 10 latest (type to search all tasks) or a new one;\nEsc cancels and changes nothing");
        keys.AddRow("P", "Pause / resume the current task");
        keys.AddRow("E", "End the current task and keep the window open; S or N starts the next one");
        keys.AddRow("R", "Report: today's summary, the running task counted up to now\n(then I all intervals, Q quit, any other key returns)");
        keys.AddRow("Q", "Stop, save, print today's summary and exit (Ctrl+C does the same)");
        AnsiConsole.Write(keys);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("Task names are free text, matched ignoring case and surrounding spaces.");
        AnsiConsole.MarkupLine("A task keeps the spelling it was logged with before; a new one keeps yours.");
        AnsiConsole.MarkupLine("Sleep over 5 minutes is not counted. Only one copy runs at a time.");
        AnsiConsole.MarkupLine($"Logs: [grey]{Markup.Escape(Path.Combine(logDirectory, "yyyy-MM-dd.json"))}[/]");
    }

    /// <summary>The day's summary, or a short note for an empty day. Also used by <c>TaskTracker report</c>.</summary>
    /// <remarks>With <paramref name="intervals"/>, the intervals table follows, laid out from the same widths.</remarks>
    public static void WriteReport(DaySummary day, IAnsiConsole? console = null, bool intervals = false)
    {
        console ??= AnsiConsole.Console;
        if (day.TaskCount == 0)
        {
            console.MarkupLine($"[grey]Nothing tracked on {day.Date:ddd dd MMM yyyy}.[/]");
            return;
        }

        var layout = ReportLayout.For(day);
        WriteSummary(day, layout, console);
        if (intervals && day.Intervals.Count > 0)
            WriteIntervals(day, layout, console);
    }

    /// <summary>
    /// Column widths shared by the summary and the intervals table, so that the two line up when printed
    /// one after the other: same widths for the same columns, and the same total width. The intervals table
    /// has no Intervals column, so its Task column takes that column's place (its width plus padding and border).
    /// </summary>
    private sealed record ReportLayout(int Task, int Intervals, int Total)
    {
        public const int Start = 11; // "First start"
        public const int End = 8;    // "Last end"
        private const int ColumnChrome = 3; // padding left and right, plus the border between columns

        public int IntervalsTask => Task + Intervals + ColumnChrome;

        public static ReportLayout For(DaySummary day)
        {
            var intervals = Math.Max("Intervals".Length, day.Tasks.Select(t => t.IntervalCount.ToString().Length).DefaultIfEmpty(0).Max());
            var task = new[] { "Task", TasksFooter(day) }
                .Concat(day.Tasks.Select(t => t.Task))
                .Max(s => s.GetCellWidth());
            // The day's total is the longest duration: every task total and interval is part of it.
            var total = Math.Max("Total".Length, FormatDuration(day.Total).Length);
            return new ReportLayout(task, intervals, total);
        }
    }

    private static string TasksFooter(DaySummary day) => $"{day.TaskCount} task(s)";

    private static string IntervalsFooter(DaySummary day) => $"{day.Intervals.Count} interval(s)";

    private static void WriteSummary(DaySummary day, ReportLayout layout, IAnsiConsole console)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"{day.Date:dddd, dd MMM yyyy}")
            .AddColumn(new TableColumn("Task").Width(layout.Task).Footer($"[bold]{TasksFooter(day)}[/]"))
            .AddColumn(new TableColumn("Intervals").Width(layout.Intervals).RightAligned())
            .AddColumn(new TableColumn("First start").Width(ReportLayout.Start).Centered())
            .AddColumn(new TableColumn("Last end").Width(ReportLayout.End).Centered())
            .AddColumn(new TableColumn("Total").Width(layout.Total).RightAligned().Footer($"[bold]{FormatDuration(day.Total)}[/]"));

        foreach (var task in day.Tasks)
        {
            table.AddRow(
                Markup.Escape(task.Task),
                task.IntervalCount.ToString(),
                $"{task.FirstStart:HH:mm}",
                $"{task.LastEnd:HH:mm}",
                FormatDuration(task.Total));
        }
        console.Write(table);
    }

    /// <summary>Every interval of the day in time order, for <c>I</c> in the report. <c>report --intervals</c> goes through <see cref="WriteReport"/>.</summary>
    public static void WriteIntervals(DaySummary day, IAnsiConsole? console = null) =>
        WriteIntervals(day, ReportLayout.For(day), console ?? AnsiConsole.Console);

    private static void WriteIntervals(DaySummary day, ReportLayout layout, IAnsiConsole console)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"Intervals, {day.Date:ddd dd MMM yyyy}")
            .AddColumn(new TableColumn("Task").Width(layout.IntervalsTask).Footer($"[bold]{IntervalsFooter(day)}[/]"))
            .AddColumn(new TableColumn("Start").Width(ReportLayout.Start).Centered())
            .AddColumn(new TableColumn("End").Width(ReportLayout.End).Centered())
            .AddColumn(new TableColumn("Time").Width(layout.Total).RightAligned().Footer($"[bold]{FormatDuration(day.Total)}[/]"));

        foreach (var interval in day.Intervals)
        {
            table.AddRow(
                Markup.Escape(interval.Task),
                $"{interval.Start:HH:mm}",
                $"{interval.End:HH:mm}",
                FormatDuration(interval.Duration));
        }
        console.Write(table);
    }

    internal static string FormatDuration(TimeSpan duration) => $"{(int)duration.TotalHours:00}:{duration.Minutes:00}";
}
