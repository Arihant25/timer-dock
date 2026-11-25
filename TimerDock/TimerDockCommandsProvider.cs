using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace TimerDock;

internal static class Ids
{
    public const string Provider = "timerdock";
    public const string Page = "timerdock.page";

    /// <summary>
    /// Command Palette resolves pinned Dock bands by this ID alone, ignoring the provider, so the
    /// Store and sideloaded builds use different IDs. Otherwise a pin left over from one build
    /// hides the other build's band from the Dock's Edit list.
    /// </summary>
    public static string Band => BandId.Value;

    private static readonly Lazy<string> BandId = new(() => IsStoreBuild() ? "timerdock.band" : "timerdock.sideloaded.band");

    private static bool IsStoreBuild()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch
        {
            return false;
        }
    }
}

internal static class Glyphs
{
    public const string Stopwatch = "";
    public const string Countdown = "";
    public const string Done = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Reset = "";
    public const string Stop = "";
    public const string Add = "";
    public const string Flag = "";
}

/// <summary>Runs an engine action. Returns to the page (refreshed) when used there, closes otherwise.</summary>
internal sealed partial class ActionCommand : InvokableCommand
{
    private readonly Action _action;
    private readonly bool _keepOpen;

    public ActionCommand(string name, string glyph, Action action, bool keepOpen = false)
    {
        Name = name;
        Icon = new IconInfo(glyph);
        _action = action;
        _keepOpen = keepOpen;
    }

    public override ICommandResult Invoke()
    {
        _action();
        return _keepOpen ? CommandResult.KeepOpen() : CommandResult.Dismiss();
    }
}

/// <summary>
/// The Command Palette page behind the Dock readout. Type a length ("25", "90s", "1h30m", "1:30",
/// "10m tea") to start a countdown, or pick a preset, the stopwatch, or a control for the current one.
/// </summary>
internal sealed partial class TimerPage : DynamicListPage
{
    private static readonly int[] PresetMinutes = [1, 3, 5, 10, 15, 20, 25, 30, 45, 60, 90];

    public TimerPage()
    {
        Id = Ids.Page;
        Name = "Open";
        Title = "Timer & Stopwatch";
        Icon = new IconInfo(Glyphs.Stopwatch);
        PlaceholderText = "Type a timer length, e.g. 25, 90s, 1h30m, 10m tea";
        TimerEngine.StateChanged += Refresh;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    /// <summary>Stops refreshing this page once its Command Palette connection is replaced.</summary>
    public void Detach() => TimerEngine.StateChanged -= Refresh;

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();
        var query = SearchText?.Trim() ?? string.Empty;

        if (TimerEngine.TryParse(query, out var typed, out var label))
        {
            var name = string.IsNullOrEmpty(label) ? $"Start {TimerEngine.Describe(typed)} timer" : $"Start {TimerEngine.Describe(typed)} timer: {label}";
            items.Add(new ListItem(new ActionCommand("Start", Glyphs.Play, () => TimerEngine.StartCountdown(typed, label)))
            {
                Title = name,
                Subtitle = $"Ends at {DateTime.Now + typed:t}",
                Icon = new IconInfo(Glyphs.Countdown),
            });
        }

        var rest = new List<ListItem>();
        rest.AddRange(LapItems());
        rest.AddRange(CurrentControls());
        rest.Add(new ListItem(new ActionCommand("Start", Glyphs.Play, TimerEngine.StartStopwatch))
        {
            Title = TimerEngine.Mode == TimerMode.Stopwatch ? "Restart stopwatch" : "Start stopwatch",
            Subtitle = "Count up from 00:00",
            Icon = new IconInfo(Glyphs.Stopwatch),
            Section = "New",
        });
        foreach (var min in PresetMinutes)
        {
            var d = TimeSpan.FromMinutes(min);
            rest.Add(new ListItem(new ActionCommand("Start", Glyphs.Play, () => TimerEngine.StartCountdown(d)))
            {
                Title = $"{TimerEngine.Describe(d)} timer",
                Icon = new IconInfo(Glyphs.Countdown),
                Section = "New",
            });
        }

        // A typed length already gives the one useful item; otherwise filter the list by the text.
        items.AddRange(items.Count > 0 || query.Length == 0
            ? rest
            : rest.Where(i => i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)));

        return [.. items];
    }

    private void Refresh()
    {
        try
        {
            RaiseItemsChanged();
        }
        catch
        {
            // Page no longer shown.
        }
    }

    private static IEnumerable<ListItem> CurrentControls()
    {
        var mode = TimerEngine.Mode;
        if (mode == TimerMode.Idle)
        {
            yield break;
        }

        var what = mode == TimerMode.Stopwatch ? "stopwatch" : "timer";
        var status = TimerEngine.Finished ? "Finished" : TimerEngine.Running ? "Running" : "Paused";
        var heading = mode == TimerMode.Countdown && !string.IsNullOrEmpty(TimerEngine.Label) ? $"Current: {TimerEngine.Label}" : "Current";

        if (!TimerEngine.Finished)
        {
            yield return new ListItem(new ActionCommand(TimerEngine.Running ? "Pause" : "Resume", TimerEngine.Running ? Glyphs.Pause : Glyphs.Play, TimerEngine.Running ? TimerEngine.Pause : TimerEngine.Resume, keepOpen: true))
            {
                Title = $"{(TimerEngine.Running ? "Pause" : "Resume")} {what}",
                Subtitle = $"{status} · {TimerEngine.Display}",
                Section = heading,
            };
        }

        if (mode == TimerMode.Stopwatch && TimerEngine.Running)
        {
            yield return new ListItem(new ActionCommand("Lap", Glyphs.Flag, TimerEngine.Lap, keepOpen: true))
            {
                Title = "Lap",
                Subtitle = "Record the current time",
                Section = heading,
            };
        }
        else if (mode == TimerMode.Countdown)
        {
            foreach (var extra in new[] { 1, 5 })
            {
                yield return new ListItem(new ActionCommand("Add", Glyphs.Add, () => TimerEngine.Extend(TimeSpan.FromMinutes(extra)), keepOpen: true))
                {
                    Title = $"Add {extra} min",
                    Subtitle = TimerEngine.Finished ? "Restart with this much left" : $"{TimerEngine.Display} left",
                    Section = heading,
                };
            }
        }

        yield return new ListItem(new ActionCommand(TimerEngine.Finished ? "Dismiss" : "Reset", TimerEngine.Finished ? Glyphs.Stop : Glyphs.Reset, TimerEngine.Reset, keepOpen: true))
        {
            Title = TimerEngine.Finished ? "Dismiss" : $"Reset {what}",
            Subtitle = TimerEngine.Finished ? "Clear the finished timer from the Dock" : "Stop and clear",
            Section = heading,
        };
    }

    private static IEnumerable<ListItem> LapItems()
    {
        var laps = TimerEngine.Laps;
        for (var i = laps.Count - 1; i >= 0; i--)
        {
            var split = laps[i] - (i > 0 ? laps[i - 1] : TimeSpan.Zero);
            yield return new ListItem(new NoOpCommand())
            {
                Title = $"Lap {i + 1}   {TimerEngine.Format(laps[i])}",
                Subtitle = $"+{TimerEngine.Format(split)}",
                Icon = new IconInfo(Glyphs.Flag),
                Section = "Laps",
            };
        }
    }
}

/// <summary>Dock readout: "12:34" with a stopwatch or timer icon. Opens the page.</summary>
internal sealed partial class ReadoutItem : ListItem
{
    private readonly TimerPage _page;

    public ReadoutItem()
        : this(new TimerPage { Name = string.Empty })
    {
    }

    private ReadoutItem(TimerPage page)
        : base(page)
    {
        _page = page;
        Update();
        TimerEngine.Changed += Update;
    }

    public void Detach()
    {
        TimerEngine.Changed -= Update;
        _page.Detach();
    }

    private void Update()
    {
        var mode = TimerEngine.Mode;
        var title = TimerEngine.Display;
        if (title != Title)
        {
            Title = title;
        }

        var subtitle = mode switch
        {
            TimerMode.Idle => "Start a timer or stopwatch",
            TimerMode.Stopwatch => TimerEngine.Running ? "Stopwatch" : "Stopwatch (paused)",
            _ when TimerEngine.Finished => string.IsNullOrEmpty(TimerEngine.Label) ? "Timer finished" : $"{TimerEngine.Label} finished",
            _ => $"{(string.IsNullOrEmpty(TimerEngine.Label) ? "Timer" : TimerEngine.Label)} · {TimerEngine.Describe(TimerEngine.Duration)}{(TimerEngine.Running ? string.Empty : " (paused)")}",
        };
        if (subtitle != Subtitle)
        {
            Subtitle = subtitle;
        }

        var glyph = mode switch
        {
            TimerMode.Countdown when TimerEngine.Finished => Glyphs.Done,
            TimerMode.Countdown => Glyphs.Countdown,
            _ => Glyphs.Stopwatch,
        };
        if (Icon?.Dark?.Icon != glyph)
        {
            Icon = new IconInfo(glyph);
        }
    }
}

public sealed partial class TimerDockCommandsProvider : CommandProvider
{
    private readonly Lock _lock = new();
    private readonly TimerPage _topLevelPage = new();
    private ReadoutItem? _readout;

    public TimerDockCommandsProvider()
    {
        Id = Ids.Provider;
        DisplayName = "Timer Dock";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
    }

    public override ICommandItem[] TopLevelCommands() =>
    [
        new CommandItem(_topLevelPage)
        {
            Title = "Timer & Stopwatch",
            Subtitle = "Start a countdown or stopwatch shown on the Dock",
        },
    ];

    // Fresh objects for each Command Palette connection, so a Command Palette that has exited
    // can't block updates to the running one (SDK 0.12 stops at the first failing subscriber).
    public override ICommandItem[]? GetDockBands()
    {
        var readout = new ReadoutItem();
        lock (_lock)
        {
            _readout?.Detach();
            _readout = readout;
        }

        return [new WrappedDockItem([readout], Ids.Band, "Timer & Stopwatch")];
    }
}
