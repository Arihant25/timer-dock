using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace TimerDock;

internal enum TimerMode
{
    Idle,
    Stopwatch,
    Countdown,
}

/// <summary>
/// The single stopwatch / countdown shared by the Dock and the Command Palette page. Time is kept
/// as "elapsed before the last resume" plus "time since the last resume", so pausing is exact and
/// the state survives Command Palette or the extension restarting (it's saved on every change).
/// All state changes happen under one lock; events are raised outside it.
/// </summary>
internal static partial class TimerEngine
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(99);

    /// <summary>A countdown found already over by more than this when the extension starts is shown as finished without ringing.</summary>
    private static readonly TimeSpan LateRingLimit = TimeSpan.FromMinutes(1);

    private static readonly Lock Gate = new();
    private static readonly string Folder = DataFolder();
    private static readonly string StatePath = Path.Combine(Folder, "state.json");
    private static readonly Timer Ticker = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly List<TimeSpan> LapList = [];

    private static TimeSpan _banked;
    private static DateTime? _resumedAtUtc;
    private static string _lastShown = string.Empty;
    private static int _ticking;

    /// <summary>Raised when anything visible changes: state, or the displayed second.</summary>
    public static event Action? Changed;

    /// <summary>Raised only on state changes (start, pause, lap...), not every second.</summary>
    public static event Action? StateChanged;

    public static TimerMode Mode { get; private set; }

    public static bool Running => _resumedAtUtc is not null;

    /// <summary>A countdown that hit zero and hasn't been dismissed yet.</summary>
    public static bool Finished { get; private set; }

    public static TimeSpan Duration { get; private set; }

    public static string Label { get; private set; } = string.Empty;

    public static IReadOnlyList<TimeSpan> Laps
    {
        get
        {
            lock (Gate)
            {
                return [.. LapList];
            }
        }
    }

    public static TimeSpan Elapsed
    {
        get
        {
            lock (Gate)
            {
                return ElapsedUnlocked();
            }
        }
    }

    public static TimeSpan Remaining
    {
        get
        {
            lock (Gate)
            {
                var left = Duration - ElapsedUnlocked();
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }
    }

    static TimerEngine()
    {
        lock (Gate)
        {
            Load();
            ArmTicker();
        }
    }

    /// <summary>What the Dock shows: count-up for the stopwatch, time left (rounded up) for a countdown.</summary>
    public static string Display
    {
        get
        {
            lock (Gate)
            {
                return DisplayUnlocked();
            }
        }
    }

    public static void StartStopwatch() => Change(() =>
    {
        Clear();
        Mode = TimerMode.Stopwatch;
        _resumedAtUtc = DateTime.UtcNow;
    });

    public static void StartCountdown(TimeSpan duration, string label = "")
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        Change(() =>
        {
            Clear();
            Mode = TimerMode.Countdown;
            Duration = duration > MaxLength ? MaxLength : duration;
            Label = label.Trim();
            _resumedAtUtc = DateTime.UtcNow;
        });
    }

    public static void Pause()
    {
        Alarm.Request? ring = null;
        Change(() =>
        {
            if (Mode == TimerMode.Idle || Finished || _resumedAtUtc is not { } since)
            {
                return;
            }

            _banked += DateTime.UtcNow - since;
            _resumedAtUtc = null;

            // Paused in the instant between reaching zero and the ticker noticing: finish it now.
            ring = FinishIfDueUnlocked();
        });
        Ring(ring);
    }

    public static void Resume() => Change(() =>
    {
        if (Mode != TimerMode.Idle && !Finished && _resumedAtUtc is null)
        {
            _resumedAtUtc = DateTime.UtcNow;
        }
    });

    public static void Reset()
    {
        Change(Clear);
        Alarm.Stop();
    }

    public static void Lap() => Change(() =>
    {
        if (Mode == TimerMode.Stopwatch && Running)
        {
            LapList.Add(ElapsedUnlocked());
        }
    });

    /// <summary>Adds time to the running countdown (or revives a finished one with that much left).</summary>
    public static void Extend(TimeSpan extra)
    {
        var revived = false;
        Change(() =>
        {
            if (Mode != TimerMode.Countdown)
            {
                return;
            }

            if (Finished)
            {
                Finished = false;
                revived = true;
                _banked = Duration;
                _resumedAtUtc = DateTime.UtcNow;
            }

            Duration = Duration + extra > MaxLength ? MaxLength : Duration + extra;
        });

        if (revived)
        {
            Alarm.Stop();
        }
    }

    public static string Format(TimeSpan t, bool roundUp = false)
    {
        var ticks = Math.Max(0, t.Ticks);
        var s = roundUp ? (ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond : ticks / TimeSpan.TicksPerSecond;
        var h = s / 3600;
        var m = s / 60 % 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }

    /// <summary>Spoken-style length for menus and toasts: "25 min", "1 h 30 min", "1 min 30 s".</summary>
    public static string Describe(TimeSpan t)
    {
        var parts = new List<string>();
        if (t.TotalHours >= 1)
        {
            parts.Add($"{(int)t.TotalHours} h");
        }

        if (t.Minutes > 0)
        {
            parts.Add($"{t.Minutes} min");
        }

        if (t.Seconds > 0)
        {
            parts.Add($"{t.Seconds} s");
        }

        return parts.Count == 0 ? "0 s" : string.Join(' ', parts);
    }

    /// <summary>
    /// Reads a timer typed into Command Palette: "5" (minutes), "90s", "1h30m", "1h30", "2 min",
    /// "1:30", "1:00:00", optionally followed by a label ("25m pasta"). Never throws.
    /// </summary>
    public static bool TryParse(string text, out TimeSpan duration, out string label)
    {
        duration = TimeSpan.Zero;
        label = string.Empty;
        try
        {
            var m = DurationRegex().Match(text ?? string.Empty);
            if (!m.Success)
            {
                return false;
            }

            double seconds;
            if (m.Groups["clock"].Success)
            {
                var p = m.Groups["clock"].Value.Split(':').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                if (p.Skip(1).Any(x => x >= 60))
                {
                    return false;
                }

                seconds = p.Length == 3 ? (p[0] * 3600) + (p[1] * 60) + p[2] : (p[0] * 60) + p[1];
            }
            else if (m.Groups["num"].Success)
            {
                seconds = Num(m, "num") * 60;
            }
            else
            {
                // A bare number after hours means minutes ("1h30"), after minutes means seconds ("1m30").
                seconds = (Num(m, "h") * 3600) + ((Num(m, "m") + Num(m, "hm")) * 60) + Num(m, "s") + Num(m, "ms");
            }

            if (double.IsNaN(seconds) || seconds < 0.5 || seconds > MaxLength.TotalSeconds)
            {
                return false;
            }

            duration = TimeSpan.FromSeconds(Math.Round(seconds));
            label = m.Groups["label"].Value.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double Num(Match m, string group) =>
        m.Groups[group].Success ? double.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    // [0-9] rather than \d: \d also matches other scripts' digits, which double.Parse rejects.
    // Numbers are capped at 6 digits so nothing overflows before the 99-hour check.
    [GeneratedRegex("""
        ^\s*(?:
            (?<clock>[0-9]{1,2}(?::[0-9]{1,2}){1,2})(?=\s|$)
          | (?<num>[0-9]{1,6}(?:\.[0-9]{1,3})?)(?=\s|$)(?!\s+(?:h|hrs?|hours?|m|mins?|minutes?|s|secs?|seconds?)(?![a-z]))
          | (?=[0-9])
            (?:(?<h>[0-9]{1,3}(?:\.[0-9]{1,3})?)\s*(?:h|hrs?|hours?)(?![a-z])\s*(?:(?<hm>[0-9]{1,2})(?=\s|$))?\s*)?
            (?:(?<m>[0-9]{1,6}(?:\.[0-9]{1,3})?)\s*(?:m|mins?|minutes?)(?![a-z])\s*(?:(?<ms>[0-9]{1,2})(?=\s|$))?\s*)?
            (?:(?<s>[0-9]{1,6})\s*(?:s|secs?|seconds?)(?![a-z]))?
        )\s*(?<label>.*)$
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DurationRegex();

    private static TimeSpan ElapsedUnlocked() => _banked + (_resumedAtUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero);

    private static string DisplayUnlocked() => Mode switch
    {
        TimerMode.Stopwatch => Format(ElapsedUnlocked()),
        TimerMode.Countdown when Finished => "Time's up",
        TimerMode.Countdown => Format(Duration - ElapsedUnlocked(), roundUp: true),
        _ => string.Empty,
    };

    private static void Clear()
    {
        Mode = TimerMode.Idle;
        Finished = false;
        Duration = TimeSpan.Zero;
        Label = string.Empty;
        _banked = TimeSpan.Zero;
        _resumedAtUtc = null;
        LapList.Clear();
    }

    /// <summary>If a countdown has reached zero, marks it finished and returns what to announce.</summary>
    private static Alarm.Request? FinishIfDueUnlocked()
    {
        if (Mode != TimerMode.Countdown || Finished || ElapsedUnlocked() < Duration)
        {
            return null;
        }

        _banked = Duration;
        _resumedAtUtc = null;
        Finished = true;
        return new Alarm.Request(Label, Duration);
    }

    private static void Tick()
    {
        // Timer callbacks can overlap if Command Palette is slow to take an update; skip instead of piling up.
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Alarm.Request? ring = null;
            var changed = false;
            lock (Gate)
            {
                ring = FinishIfDueUnlocked();
                if (ring is not null)
                {
                    SaveUnlocked();
                    ArmTicker();
                }

                var shown = DisplayUnlocked();
                if (shown != _lastShown)
                {
                    _lastShown = shown;
                    changed = true;
                }
            }

            if (ring is not null)
            {
                Raise(StateChanged);
            }

            if (changed || ring is not null)
            {
                Raise(Changed);
            }

            Ring(ring);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    /// <summary>Applies a state change, then saves, re-arms the ticker and tells everyone.</summary>
    private static void Change(Action apply)
    {
        lock (Gate)
        {
            apply();
            SaveUnlocked();
            ArmTicker();
            _lastShown = DisplayUnlocked();
        }

        Raise(StateChanged);
        Raise(Changed);
    }

    private static void ArmTicker()
    {
        var running = Running;
        Ticker.Change(running ? 0 : Timeout.Infinite, running ? 250 : Timeout.Infinite);
    }

    private static void Ring(Alarm.Request? ring)
    {
        if (ring is { } r)
        {
            Alarm.Ring(r);
        }
    }

    private static void Raise(Action? evt)
    {
        if (evt is null)
        {
            return;
        }

        foreach (Action handler in evt.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
                // A Command Palette that has exited must not stop updates to the others.
            }
        }
    }

    private static void SaveUnlocked()
    {
        try
        {
            var o = new JsonObject
            {
                ["mode"] = Mode.ToString(),
                ["finished"] = Finished,
                ["durationMs"] = (long)Duration.TotalMilliseconds,
                ["label"] = Label,
                ["bankedMs"] = (long)_banked.TotalMilliseconds,
                ["resumedAtUtc"] = _resumedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                ["lapsMs"] = new JsonArray([.. LapList.Select(l => (JsonNode)(long)l.TotalMilliseconds)]),
            };

            Directory.CreateDirectory(Folder);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch
        {
            // Still works for this session.
        }
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(StatePath) || JsonNode.Parse(File.ReadAllText(StatePath)) is not JsonObject o)
            {
                return;
            }

            Mode = Enum.TryParse<TimerMode>(o["mode"]?.GetValue<string>(), out var mode) && Enum.IsDefined(mode) ? mode : TimerMode.Idle;
            Finished = Mode == TimerMode.Countdown && (o["finished"]?.GetValue<bool>() ?? false);
            Duration = TimeSpan.FromMilliseconds(o["durationMs"]?.GetValue<long>() ?? 0);
            Label = o["label"]?.GetValue<string>() ?? string.Empty;
            _banked = TimeSpan.FromMilliseconds(o["bankedMs"]?.GetValue<long>() ?? 0);
            _resumedAtUtc = Mode != TimerMode.Idle && !Finished && o["resumedAtUtc"]?.GetValue<string>() is { } r
                ? DateTime.Parse(r, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
            LapList.AddRange((o["lapsMs"] as JsonArray ?? []).Select(n => TimeSpan.FromMilliseconds(n?.GetValue<long>() ?? 0)));

            // A countdown that ended while the extension wasn't running: show it as finished, and
            // only ring (via the first tick) if it ended moments ago, not hours late.
            if (Mode == TimerMode.Countdown && !Finished && ElapsedUnlocked() >= Duration
                && (_resumedAtUtc is null || ElapsedUnlocked() - Duration > LateRingLimit))
            {
                _banked = Duration;
                _resumedAtUtc = null;
                Finished = true;
            }
        }
        catch
        {
            Clear();
        }
    }

    internal static string DataFolder()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "TimerDock");
        }
    }
}
