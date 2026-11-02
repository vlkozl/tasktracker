using Spectre.Console;
using TaskTracker.Models;
using TaskTracker.Reporting;
using TaskTracker.UI;

namespace TaskTracker.Tests;

public sealed class ReportTableTests
{
    private static readonly DateOnly Day = new(2026, 10, 24);

    [Theory]
    [InlineData("A", "B")]
    [InlineData("A much longer task name than the footer", "x")]
    public void SummaryAndIntervals_HaveTheSameWidthAndColumnBorders(string first, string second)
    {
        var log = new DayLog { Date = Day };
        log.GetOrAddTask(first).Intervals.Add(Interval(9, 0, 9, 47));
        log.GetOrAddTask(second).Intervals.Add(Interval(10, 0, 10, 5));
        log.GetOrAddTask(first).Intervals.Add(Interval(13, 0, 15, 30));
        var day = DaySummary.From(log);

        // As printed by report --intervals: the summary, then the intervals table.
        var output = Render(c => ConsoleUi.WriteReport(day, c, intervals: true));
        var split = Array.FindIndex(output, l => l.StartsWith('╰')) + 1;
        var summary = output[..split];
        var intervals = output[split..];
        Assert.Equal(intervals, Render(c => ConsoleUi.WriteIntervals(day, c))[..intervals.Length]);

        var summaryBottom = summary.Last(l => l.StartsWith('╰'));
        var intervalsBottom = intervals.Last(l => l.StartsWith('╰'));
        Assert.Equal(summaryBottom.Length, intervalsBottom.Length);
        Assert.Contains(intervals, l => l.Contains("│ Task") && l.EndsWith(" Time │"));

        // Start, End and Total line up: the borders after the Intervals column match from there on.
        var shared = summaryBottom.IndexOf('┴', summaryBottom.IndexOf('┴') + 1);
        Assert.Equal(summaryBottom[shared..], intervalsBottom[shared..]);
        Assert.Equal('┴', intervalsBottom[shared]);

        // Both tables have a footer with the day's total.
        Assert.Contains(summary, l => l.Contains("2 task(s)") && l.Contains("03:22"));
        Assert.Contains(intervals, l => l.Contains("3 interval(s)") && l.Contains("03:22"));
    }

    private static string[] Render(Action<IAnsiConsole> write)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 200;
        write(console);
        return output.ToString().Split('\n').Select(l => l.TrimEnd()).ToArray();
    }

    private static Interval Interval(int h1, int m1, int h2, int m2) => new()
    {
        Start = new DateTimeOffset(Day.ToDateTime(new TimeOnly(h1, m1)), TimeSpan.Zero),
        End = new DateTimeOffset(Day.ToDateTime(new TimeOnly(h2, m2)), TimeSpan.Zero),
    };
}
