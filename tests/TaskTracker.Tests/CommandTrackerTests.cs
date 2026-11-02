using Microsoft.Extensions.Time.Testing;
using TaskTracker.Storage;
using TaskTracker.Tracking;

namespace TaskTracker.Tests;

public sealed class CommandTrackerTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 24);
    private readonly TempStore _temp = new();
    private readonly FakeTimeProvider _time;
    private readonly FakePowerHistory _power = new();
    private readonly OpenTaskStore _open;
    private readonly CommandTracker _commands;

    public CommandTrackerTests()
    {
        _time = new FakeTimeProvider(At(9, 0));
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _open = new OpenTaskStore(Path.Combine(_temp.Directory, "current.json"));
        _commands = new CommandTracker(_temp.Store, _open, _power, _time);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Start_WritesOnlyTheOpenTask()
    {
        _commands.Start("  Bug 42 ");

        Assert.Equal("Bug 42", _open.Load()!.Task);
        Assert.Equal(At(9, 0), _open.Load()!.Start);
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void End_WritesIntervalAndClearsOpenTask()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(90));

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromMinutes(90), ended.Worked);
        Assert.Null(_open.Load());
        var interval = Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
        Assert.Equal(At(9, 0), interval.Start);
        Assert.Equal(At(10, 30), interval.End);
    }

    [Fact]
    public void End_WithNothingRunning_ReturnsNull()
    {
        Assert.Null(_commands.End());
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void Start_AnotherTask_EndsTheFirst()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(30));

        var result = _commands.Start("B");

        Assert.Equal("A", result.Ended!.Task);
        Assert.Equal(TimeSpan.FromMinutes(30), result.Ended.Worked);
        Assert.Equal("B", _open.Load()!.Task);
        Assert.Equal(At(9, 30), _open.Load()!.Start);
        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day, "A"));
    }

    [Fact]
    public void Start_SameTask_KeepsOriginalStart()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(30));

        var result = _commands.Start("a");

        Assert.True(result.AlreadyRunning);
        Assert.Equal(At(9, 0), _open.Load()!.Start);
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void End_LeavesOutTimeThePcWasOff()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(3));
        _power.Periods.Add(new TimeRange(At(10, 0), At(11, 0)));
        _power.Periods.Add(new TimeRange(At(10, 30), At(11, 15))); // overlapping, e.g. sleep then reboot

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromMinutes(105), ended.Worked);
        Assert.Equal(TimeSpan.FromMinutes(75), ended.Off);
        var intervals = Assert.Single(_temp.Store.Load(Day).Tasks).Intervals;
        Assert.Equal([At(9, 0), At(11, 15)], intervals.Select(i => i.Start));
        Assert.Equal([At(10, 0), At(12, 0)], intervals.Select(i => i.End));
    }

    [Fact]
    public void End_OffPeriodsInAnotherTimeZone_AreConvertedToLocalTime()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(2));
        var plus2 = TimeSpan.FromHours(2);
        _power.Periods.Add(new TimeRange(
            new DateTimeOffset(2026, 10, 24, 11, 30, 0, plus2), new DateTimeOffset(2026, 10, 24, 12, 0, 0, plus2)));

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromMinutes(90), ended.Worked);
        Assert.All(_temp.Store.Load(Day).Tasks[0].Intervals, i => Assert.Equal(TimeSpan.Zero, i.Start.Offset));
    }

    [Fact]
    public void End_OvernightSleep_WritesBothDays()
    {
        _time.SetUtcNow(At(22, 0));
        _commands.Start("Late");
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
        _power.Periods.Add(new TimeRange(At(23, 0), new DateTimeOffset(2026, 10, 25, 8, 30, 0, TimeSpan.Zero)));

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromMinutes(90), ended.Worked);
        Assert.Equal(TimeSpan.FromMinutes(60), Total(Day, "Late"));
        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day.AddDays(1), "Late"));
    }

    [Fact]
    public void End_AcrossMidnight_SplitsAtMidnight()
    {
        _time.SetUtcNow(At(23, 40));
        _commands.Start("Late");
        _time.Advance(TimeSpan.FromMinutes(30));

        _commands.End();

        var before = Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
        var after = Assert.Single(Assert.Single(_temp.Store.Load(Day.AddDays(1)).Tasks).Intervals);
        Assert.Equal(TimeSpan.FromMinutes(20), before.Duration);
        Assert.Equal(before.End, after.Start);
        Assert.Equal(TimeSpan.FromMinutes(10), after.Duration);
    }

    [Fact]
    public void End_UnknownPowerHistory_CountsEverythingAndSaysSo()
    {
        _power.Unknown = true;
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(45));

        var ended = _commands.End()!;

        Assert.True(ended.OffUnknown);
        Assert.Equal(TimeSpan.FromMinutes(45), ended.Worked);
    }

    [Fact]
    public void End_AddsToTimeTrackedInTheApp()
    {
        var tracker = new Tracker(_temp.Store, _time);
        tracker.Start("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        tracker.Tick();
        tracker.Stop();

        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(20));
        _commands.End();

        var task = Assert.Single(_temp.Store.Load(Day).Tasks);
        Assert.Equal("a", task.Task);
        Assert.Equal(2, task.Intervals.Count);
        Assert.Equal(21 * 60, task.TotalSeconds);
    }

    [Fact]
    public void End_RepeatedAfterACrash_DoesNotCountTwice()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(10));
        var open = _open.Load()!;
        _commands.End();

        _open.Save(open); // as if the process died before current.json was deleted
        _time.Advance(TimeSpan.FromMinutes(5));
        _commands.End();

        var interval = Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
        Assert.Equal(TimeSpan.FromMinutes(15), interval.Duration);
    }

    [Fact]
    public void End_RepeatedWithDifferentPowerHistory_DoesNotCountTwice()
    {
        _power.Unknown = true;
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(3));
        var open = _open.Load()!;
        _commands.End(); // writes 9:00-12:00

        _open.Save(open); // crashed before current.json was deleted; this time sleep is known
        _power.Unknown = false;
        _power.Periods.Add(new TimeRange(At(10, 0), At(11, 0)));
        _time.Advance(TimeSpan.FromMinutes(30));
        _commands.End();

        var intervals = Assert.Single(_temp.Store.Load(Day).Tasks).Intervals;
        Assert.Equal([At(9, 0), At(11, 0)], intervals.Select(i => i.Start));
        Assert.Equal([At(10, 0), At(12, 30)], intervals.Select(i => i.End));
    }

    [Fact]
    public void End_OffPeriodsOverlappingStartAndEnd_AreClipped()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(2));
        _power.Periods.Add(new TimeRange(At(8, 0), At(9, 15)));
        _power.Periods.Add(new TimeRange(At(10, 45), At(12, 0)));

        var ended = _commands.End()!;

        var interval = Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
        Assert.Equal(At(9, 15), interval.Start);
        Assert.Equal(At(10, 45), interval.End);
        Assert.Equal(TimeSpan.FromMinutes(30), ended.Off);
    }

    [Fact]
    public void End_ReversedOffPeriod_IsIgnored()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(2));
        _power.Periods.Add(new TimeRange(At(10, 30), At(9, 30))); // wake logged before sleep

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromHours(2), ended.Worked);
        Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
    }

    [Fact]
    public void End_ClockSetBack_ThrowsAndKeepsOpenTask()
    {
        var clock = new SettableTime(At(10, 0));
        var commands = new CommandTracker(_temp.Store, _open, _power, clock);
        commands.Start("A");
        clock.Now = At(9, 0);

        var ex = Assert.Throws<InvalidDataException>(() => commands.End());

        Assert.Contains(_open.Path, ex.Message);
        Assert.NotNull(_open.Load());
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void End_SmallClockCorrection_EndsWithNothingRecorded()
    {
        var clock = new SettableTime(At(10, 0));
        var commands = new CommandTracker(_temp.Store, _open, _power, clock);
        commands.Start("A");
        clock.Now = At(10, 0).AddSeconds(-3);

        Assert.Equal(TimeSpan.Zero, commands.End()!.Worked);
        Assert.Null(_open.Load());
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void End_OpenLongerThanSevenDays_ThrowsAndWritesNothing()
    {
        _commands.Start("A");
        _time.Advance(CommandTracker.MaxOpen + TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidDataException>(() => _commands.End());

        Assert.NotNull(_open.Load());
        Assert.Empty(Directory.GetFiles(_temp.Directory, "????-??-??.json"));
    }

    [Fact]
    public void Start_AnotherTaskAcrossMidnight_SplitsTheFirst()
    {
        _time.SetUtcNow(At(23, 30));
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(1));

        _commands.Start("B");

        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day, "A"));
        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day.AddDays(1), "A"));
        Assert.Equal("B", _open.Load()!.Task);
    }

    [Fact]
    public void End_AcrossDaylightSavingEnd_CountsRealTime()
    {
        // Berlin, 25 Oct 2026: clocks go back from 03:00 CEST to 02:00 CET.
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero)); // 01:30 CEST
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(3)); // 03:30 CET

        var ended = _commands.End()!;

        Assert.Equal(TimeSpan.FromHours(3), ended.Worked);
        var interval = Assert.Single(Assert.Single(_temp.Store.Load(Day.AddDays(1)).Tasks).Intervals);
        Assert.Equal(TimeSpan.FromHours(2), interval.Start.Offset);
        Assert.Equal(TimeSpan.FromHours(1), interval.End.Offset);
        Assert.False(File.Exists(_temp.Store.PathFor(Day)));
    }

    [Fact]
    public void End_AcrossLocalMidnight_SplitsAtLocalNotUtcMidnight()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"));
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 24, 21, 30, 0, TimeSpan.Zero)); // 23:30 CEST
        _commands.Start("A");
        _time.Advance(TimeSpan.FromHours(1));

        _commands.End();

        var before = Assert.Single(Assert.Single(_temp.Store.Load(Day).Tasks).Intervals);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2)), before.End);
        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day.AddDays(1), "A"));
    }

    [Fact]
    public void Start_TaskSeenBefore_KeepsItsSpelling()
    {
        _commands.Start("TASK-1123");
        _time.Advance(TimeSpan.FromMinutes(10));
        _commands.End();
        _time.Advance(TimeSpan.FromDays(2));

        _commands.Start("task-1123");
        Assert.Equal("TASK-1123", _open.Load()!.Task);

        _commands.Start(" brand New ");
        Assert.Equal("brand New", _open.Load()!.Task);
    }

    [Fact]
    public void Start_AnotherTaskWhileAnOldLogIsLocked_StillSwitches()
    {
        _commands.Start("A");
        _time.Advance(TimeSpan.FromMinutes(30));
        var old = new Models.DayLog { Date = Day.AddDays(-1) };
        _temp.Store.Save(old);

        StartedTask result;
        using (File.Open(_temp.Store.PathFor(Day.AddDays(-1)), FileMode.Open, FileAccess.Read, FileShare.None))
            result = _commands.Start("B");

        Assert.Equal(TimeSpan.FromMinutes(30), result.Ended!.Worked);
        Assert.Equal("B", _open.Load()!.Task);
        Assert.Equal(TimeSpan.FromMinutes(30), Total(Day, "A"));
    }

    [Fact]
    public void Load_OpenTaskWithoutStart_Throws()
    {
        Directory.CreateDirectory(_temp.Directory);
        File.WriteAllText(_open.Path, """{ "task": "A" }""");

        var ex = Assert.Throws<InvalidDataException>(() => _commands.End());
        Assert.Contains("delete it", ex.Message);
    }

    [Fact]
    public void Load_CorruptOpenTask_Throws()
    {
        Directory.CreateDirectory(_temp.Directory);
        File.WriteAllText(_open.Path, "{ not json");

        Assert.Throws<InvalidDataException>(() => _commands.Current);
    }

    private TimeSpan Total(DateOnly day, string task) =>
        TimeSpan.FromSeconds(_temp.Store.Load(day).FindTask(task)!.TotalSeconds);

    [Fact]
    public void StartCommand_RefusesANameOverTheLimit_AndStartsNothing()
    {
        var tooLong = new string('x', TaskTracker.Models.DayLog.MaxNameLength + 1);

        Assert.Equal(1, TaskTracker.UI.CommandLine.Run(["start", tooLong], _temp.Store, _commands, _time));
        Assert.Null(_open.Load());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void StartCommand_AcceptsALongNameAlreadyLogged(int daysAgo)
    {
        var longName = new string('x', TaskTracker.Models.DayLog.MaxNameLength + 1);
        var old = new Models.DayLog { Date = Day.AddDays(daysAgo) };
        old.GetOrAddTask(longName).Intervals.Add(new Models.Interval { Start = At(7, 0).AddDays(daysAgo), End = At(8, 0).AddDays(daysAgo) });
        _temp.Store.Save(old);

        Assert.Equal(0, TaskTracker.UI.CommandLine.Run(["start", longName.ToUpperInvariant()], _temp.Store, _commands, _time));
        Assert.Equal(longName, _open.Load()!.Task);
    }

    [Theory]
    [InlineData(50, false)]
    [InlineData(51, true)]
    public void IsTooLong_CountsCharactersAsShown_AndIgnoresSurroundingSpaces(int letters, bool tooLong)
    {
        Assert.Equal(tooLong, TaskTracker.Models.DayLog.IsTooLong("  " + new string('a', letters) + "  "));
        // An emoji is two chars in .NET but one character on screen.
        Assert.Equal(tooLong, TaskTracker.Models.DayLog.IsTooLong(string.Concat(Enumerable.Repeat("😀", letters))));
    }

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 24, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A clock that can be set backwards, which <see cref="FakeTimeProvider"/> can't do.</summary>
    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakePowerHistory : IPowerHistory
    {
        public List<TimeRange> Periods { get; } = [];
        public bool Unknown { get; set; }

        public IReadOnlyList<TimeRange>? OffPeriods(DateTimeOffset from, DateTimeOffset to) => Unknown ? null : Periods;
    }
}
