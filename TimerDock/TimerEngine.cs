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
/// </summary>
internal static partial class TimerEngine
{
    private static readonly Lock Gate = new();
    private static readonly string Folder = DataFolder();
    private static readonly string StatePath = Path.Combine(Folder, "state.json");
    private static readonly Timer Ticker = new(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    private static readonly List<TimeSpan> LapList = [];

    private static TimeSpan _banked;
    private static DateTime? _resumedAtUtc;
    private static string _lastShown = string.Empty;

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
            var left = Duration - Elapsed;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    static TimerEngine()
    {
        Load();
        if (Running)
        {
            Ticker.Change(0, 250);
        }
    }

    /// <summary>What the Dock shows: count-up for the stopwatch, time left (rounded up) for a countdown.</summary>
    public static string Display => Mode switch
    {
        TimerMode.Stopwatch => Format(Elapsed),
        TimerMode.Countdown when Finished => "Time's up",
        TimerMode.Countdown => Format(Remaining, roundUp: true),
        _ => string.Empty,
    };

    public static void StartStopwatch()
    {
        lock (Gate)
        {
            Clear();
            Mode = TimerMode.Stopwatch;
            _resumedAtUtc = DateTime.UtcNow;
        }

        Commit();
    }

    public static void StartCountdown(TimeSpan duration, string label = "")
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (Gate)
        {
            Clear();
            Mode = TimerMode.Countdown;
            Duration = duration;
            Label = label.Trim();
            _resumedAtUtc = DateTime.UtcNow;
        }

        Commit();
    }

    /// <summary>The Dock's play/pause button: starts a stopwatch from idle, dismisses a finished countdown.</summary>
    public static void Toggle()
    {
        if (Mode == TimerMode.Idle)
        {
            StartStopwatch();
            return;
        }

        if (Finished)
        {
            Reset();
            return;
        }

        lock (Gate)
        {
            if (_resumedAtUtc is { } since)
            {
                _banked += DateTime.UtcNow - since;
                _resumedAtUtc = null;
            }
            else
            {
                _resumedAtUtc = DateTime.UtcNow;
            }
        }

        Commit();
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Clear();
        }

        Alarm.Stop();
        Commit();
    }

    public static void Lap()
    {
        lock (Gate)
        {
            if (Mode != TimerMode.Stopwatch)
            {
                return;
            }

            LapList.Add(ElapsedUnlocked());
        }

        Commit();
    }

    /// <summary>Adds time to the running countdown (or revives a finished one with that much left).</summary>
    public static void Extend(TimeSpan extra)
    {
        lock (Gate)
        {
            if (Mode != TimerMode.Countdown)
            {
                return;
            }

            if (Finished)
            {
                Finished = false;
                Duration = ElapsedUnlocked() + extra;
                _resumedAtUtc ??= DateTime.UtcNow;
            }
            else
            {
                Duration += extra;
            }
        }

        Alarm.Stop();
        Commit();
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

    /// <summary>Spoken-style length for menus and toasts: "25 min", "1 h 30 min", "90 s" → "1 min 30 s".</summary>
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
    /// Reads a timer typed into Command Palette: "5" (minutes), "90s", "1h30m", "2 min", "1:30",
    /// "1:00:00", optionally followed by a label ("25m pasta").
    /// </summary>
    public static bool TryParse(string text, out TimeSpan duration, out string label)
    {
        duration = TimeSpan.Zero;
        label = string.Empty;
        var m = DurationRegex().Match(text ?? string.Empty);
        if (!m.Success)
        {
            return false;
        }

        var inv = CultureInfo.InvariantCulture;
        if (m.Groups["clock"].Success)
        {
            var p = m.Groups["clock"].Value.Split(':').Select(x => int.Parse(x, inv)).ToArray();
            duration = p.Length == 3 ? new TimeSpan(p[0], p[1], p[2]) : new TimeSpan(0, p[0], p[1]);
        }
        else if (m.Groups["num"].Success)
        {
            duration = TimeSpan.FromMinutes(double.Parse(m.Groups["num"].Value, inv));
        }
        else
        {
            double G(string g) => m.Groups[g].Success ? double.Parse(m.Groups[g].Value, inv) : 0;
            duration = TimeSpan.FromHours(G("h")) + TimeSpan.FromMinutes(G("m")) + TimeSpan.FromSeconds(G("s"));
        }

        duration = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));
        label = m.Groups["label"].Value.Trim();
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromHours(99);
    }

    [GeneratedRegex("""
        ^\s*(?:
            (?<clock>\d{1,2}(?::\d{1,2}){1,2})(?=\s|$)
          | (?<num>\d+(?:\.\d+)?)(?=\s|$)(?!\s+(?:h|hrs?|hours?|m|mins?|minutes?|s|secs?|seconds?)(?![a-z]))
          | (?=\d)
            (?:(?<h>\d+(?:\.\d+)?)\s*(?:h|hrs?|hours?)(?![a-z])\s*)?
            (?:(?<m>\d+(?:\.\d+)?)\s*(?:m|mins?|minutes?)(?![a-z])\s*)?
            (?:(?<s>\d+)\s*(?:s|secs?|seconds?)(?![a-z]))?
        )\s*(?<label>.*)$
        """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex DurationRegex();

    private static TimeSpan ElapsedUnlocked() => _banked + (_resumedAtUtc is { } since ? DateTime.UtcNow - since : TimeSpan.Zero);

    private static void Clear()
    {
        if (Finished)
        {
            Alarm.Stop();
        }

        Mode = TimerMode.Idle;
        Finished = false;
        Duration = TimeSpan.Zero;
        Label = string.Empty;
        _banked = TimeSpan.Zero;
        _resumedAtUtc = null;
        LapList.Clear();
    }

    private static void Tick()
    {
        var finishedNow = false;
        lock (Gate)
        {
            if (Mode == TimerMode.Countdown && !Finished && ElapsedUnlocked() >= Duration)
            {
                _banked = Duration;
                _resumedAtUtc = null;
                Finished = true;
                finishedNow = true;
            }
        }

        if (finishedNow)
        {
            Commit();
            Alarm.Ring(Label, Duration);
            return;
        }

        var shown = Display;
        if (shown != _lastShown)
        {
            _lastShown = shown;
            Raise(Changed);
        }
    }

    /// <summary>After a state change: save, (re)arm or stop the ticker, tell everyone.</summary>
    private static void Commit()
    {
        Save();
        Ticker.Change(Running ? 0 : Timeout.Infinite, Running ? 250 : Timeout.Infinite);
        _lastShown = Display;
        Raise(StateChanged);
        Raise(Changed);
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

    private static void Save()
    {
        try
        {
            JsonObject o;
            lock (Gate)
            {
                o = new JsonObject
                {
                    ["mode"] = Mode.ToString(),
                    ["finished"] = Finished,
                    ["durationMs"] = (long)Duration.TotalMilliseconds,
                    ["label"] = Label,
                    ["bankedMs"] = (long)_banked.TotalMilliseconds,
                    ["resumedAtUtc"] = _resumedAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                    ["lapsMs"] = new JsonArray([.. LapList.Select(l => (JsonNode)(long)l.TotalMilliseconds)]),
                };
            }

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

            Mode = Enum.TryParse<TimerMode>(o["mode"]?.GetValue<string>(), out var mode) ? mode : TimerMode.Idle;
            Finished = o["finished"]?.GetValue<bool>() ?? false;
            Duration = TimeSpan.FromMilliseconds(o["durationMs"]?.GetValue<long>() ?? 0);
            Label = o["label"]?.GetValue<string>() ?? string.Empty;
            _banked = TimeSpan.FromMilliseconds(o["bankedMs"]?.GetValue<long>() ?? 0);
            _resumedAtUtc = o["resumedAtUtc"]?.GetValue<string>() is { } r
                ? DateTime.Parse(r, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                : null;
            LapList.AddRange((o["lapsMs"] as JsonArray ?? []).Select(n => TimeSpan.FromMilliseconds(n?.GetValue<long>() ?? 0)));
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
