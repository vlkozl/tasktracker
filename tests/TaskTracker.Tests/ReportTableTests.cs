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
    [InlineData("A task name at the limit of fifty characters, okay", "x")]
    public void Report_ChartAndBothTablesLineUp(string first, string second)
    {
        var log = new DayLog { Date = Day };
        log.GetOrAddTask(first).Intervals.Add(Interval(9, 0, 9, 47));
        log.GetOrAddTask(second).Intervals.Add(Interval(10, 0, 10, 5));
        log.GetOrAddTask(first).Intervals.Add(Interval(13, 0, 15, 30));
        var day = DaySummary.From(log);

        // As printed by report --intervals: the summary, then the intervals table.
        var output = Render(c => ConsoleUi.WriteReport(day, c, intervals: true));
        var summary = output[..(Array.FindIndex(output, l => l.StartsWith('╰')) + 1)];
        var intervals = output[Array.FindIndex(output, l => l.Contains("Intervals,"))..];

        // The chart sits between the tables: longest task first, bars no wider than the tables.
        var chart = output[summary.Length..^intervals.Length].Where(l => l.Length > 0).ToArray();
        Assert.Equal(2, chart.Length);
        Assert.StartsWith(first[..Math.Min(first.Length, 20)], chart[0].TrimStart());
        Assert.EndsWith("03:17", chart[0]);
        Assert.All(chart, l => Assert.True(l.Length <= summary.Last().Length, l));
        Assert.Equal(intervals, Render(c => ConsoleUi.WriteIntervals(day, c))[..intervals.Length]);

        var summaryBottom = summary.Last(l => l.StartsWith('╰'));
        var intervalsBottom = intervals.Last(l => l.StartsWith('╰'));
        Assert.Equal(summaryBottom.Length, intervalsBottom.Length);
        Assert.Contains(intervals, l => l.Contains("│ Task") && l.EndsWith(" Time │"));

        // Start, End and Total line up: the borders after the Intervals column match from there on.
        var shared = summaryBottom.IndexOf('┴', summaryBottom.IndexOf('┴') + 1);
        Assert.Equal(summaryBottom[shared..], intervalsBottom[shared..]);
        Assert.Equal('┴', intervalsBottom[shared]);

        // Only characters every Windows console font shows the same, see AGENTS.md.
        // Letters are text, not symbols: the date title is in the user's language.
        Assert.All(output, l => Assert.True(l.All(c => c < 128 || char.IsLetter(c) || "►■║·─│╭╮╰╯├┤┬┴┼█".Contains(c)), l));

        // Both tables have a footer with the day's total.
        Assert.Contains(summary, l => l.Contains("2 task(s)") && l.Contains("03:22"));
        Assert.Contains(intervals, l => l.Contains("3 interval(s)") && l.Contains("03:22"));
    }

    /// <summary>A realistic day: short and long tasks, a name over the limit from an older log.</summary>
    private static DaySummary MockDay()
    {
        var log = new DayLog { Date = Day };
        void Add(string task, int h1, int m1, int h2, int m2) => log.GetOrAddTask(task).Intervals.Add(Interval(h1, m1, h2, m2));
        Add("standup", 9, 0, 9, 15);
        Add("report table alignment", 9, 20, 10, 45);
        Add("code review: PR #1 installer", 10, 50, 11, 30);
        Add("report table alignment", 11, 35, 12, 20);
        Add("reading: Spectre.Console docs", 13, 0, 13, 25);
        Add("bar chart in the day report", 13, 30, 15, 10);
        Add("standup", 15, 15, 15, 20);
        Add("bar chart in the day report", 15, 25, 16, 40);
        Add("code review: PR #1 installer", 16, 45, 17, 5);
        Add("symbols for every console font (cmd, PowerShell, raster fonts)", 17, 10, 18, 0);
        return DaySummary.From(log);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(100)]
    [InlineData(80)]
    public void Chart_BarsStartFromZero_OneLinePerTask(int consoleWidth)
    {
        var day = MockDay();
        var output = Render(c => ConsoleUi.WriteReport(day, c), consoleWidth);
        var tableEnd = Array.FindLastIndex(output, l => l.StartsWith('╰'));
        var chart = output[(tableEnd + 1)..].Where(l => l.Length > 0).ToArray();

        // One line per task, longest first, none wider than the table or the console.
        Assert.Equal(day.TaskCount, chart.Length);
        Assert.All(chart, l => Assert.True(l.Length <= Math.Min(output[tableEnd].Length, consoleWidth), l));
        var expectedOrder = day.Tasks.OrderByDescending(t => t.Total).Select(t => $"{(int)t.Total.TotalHours:00}:{t.Total.Minutes:00}");
        Assert.Equal(expectedOrder, chart.Select(l => l[^5..]));

        // Bars measured from zero: each is in proportion to the longest, and the shortest task still has one.
        var blocks = chart.Select(l => l.Count(c => c == '█')).ToArray();
        var totals = day.Tasks.OrderByDescending(t => t.Total).Select(t => t.Total.TotalMinutes).ToArray();
        Assert.All(blocks, b => Assert.True(b >= 1));
        for (var i = 0; i < blocks.Length; i++)
            Assert.InRange(blocks[i], Math.Max(1, blocks[0] * totals[i] / totals[0] - 1), blocks[0] * totals[i] / totals[0] + 1);

        // The bars of all tasks line up: a label never pushes its bar to the right.
        Assert.Single(chart.Select(l => l.IndexOf('█')).Distinct());
    }

    [Fact]
    public void Chart_ShortensANameTooLongForIt_AndTheTablesShowItInFull()
    {
        var output = Render(c => ConsoleUi.WriteReport(MockDay(), c, intervals: true), 100);

        Assert.Contains(output, l => l.TrimStart().StartsWith("symbols for every console font") && l.Contains("...") && l.Contains('█'));
        Assert.Contains(output, l => l.Contains("│ symbols for every console font (cmd, PowerShell, raster fonts)"));
    }

    private static string[] Render(Action<IAnsiConsole> write, int width = 200)
    {
        var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = width;
        write(console);
        return output.ToString().Split('\n').Select(l => l.TrimEnd()).ToArray();
    }

    private static Interval Interval(int h1, int m1, int h2, int m2) => new()
    {
        Start = new DateTimeOffset(Day.ToDateTime(new TimeOnly(h1, m1)), TimeSpan.Zero),
        End = new DateTimeOffset(Day.ToDateTime(new TimeOnly(h2, m2)), TimeSpan.Zero),
    };
}
