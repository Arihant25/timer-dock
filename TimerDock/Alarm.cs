using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using Windows.UI.Notifications;
using XmlDocument = Windows.Data.Xml.Dom.XmlDocument;

namespace TimerDock;

/// <summary>
/// Tells you a countdown finished: a Windows notification in the "reminder" style, which stays on
/// screen until dismissed and plays the reminder sound. If notifications are off for the app or
/// can't be shown, plays the Windows alarm sound instead. The Dock also shows "Time's up" either way.
/// </summary>
internal static partial class Alarm
{
    private const string Tag = "timerdock-done";
    private const uint SndAsync = 0x0001;
    private const uint SndFilename = 0x00020000;
    private const uint SndNodefault = 0x0002;

    /// <summary>What finished, captured when it finished.</summary>
    internal sealed record Request(string Label, TimeSpan Duration);

    // An explicit app ID: the extension runs as a COM server, where the implicit one isn't reliable.
    private static readonly Lazy<string?> Aumid = new(() =>
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
        }
        catch
        {
            return null;
        }
    });

    public static void Ring(Request request)
    {
        var label = Clean(request.Label);
        var title = string.IsNullOrEmpty(label) ? "Time's up" : $"Time's up: {label}";
        var body = $"Your {TimerEngine.Describe(request.Duration)} timer has finished.";
        if (!TryToast(title, body))
        {
            PlayFallbackSound();
        }
    }

    public static void Stop()
    {
        try
        {
            if (Aumid.Value is { } aumid)
            {
                ToastNotificationManager.History.Remove(Tag, Tag, aumid);
            }
        }
        catch
        {
            // Nothing to remove, or notifications unavailable.
        }

        _ = PlaySoundW(null, 0, 0);
    }

    private static bool TryToast(string title, string body)
    {
        try
        {
            if (Aumid.Value is not { } aumid)
            {
                return false;
            }

            var xml = $"""
                <toast scenario="reminder">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(title)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                  <audio src="ms-winsoundevent:Notification.Reminder" />
                  <actions>
                    <action content="Dismiss" arguments="dismiss" activationType="system" />
                  </actions>
                </toast>
                """;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var notifier = ToastNotificationManager.CreateToastNotifier(aumid);
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                Log($"notifications not enabled: {notifier.Setting}");
                return false;
            }

            notifier.Show(new ToastNotification(doc) { Tag = Tag, Group = Tag });
            return true;
        }
        catch (Exception ex)
        {
            Log($"toast failed: {ex}");
            return false;
        }
    }

    /// <summary>Drops characters XML can't carry (control characters in a typed label).</summary>
    private static string Clean(string s) => new([.. s.Where(XmlConvert.IsXmlChar)]);

    private static void PlayFallbackSound()
    {
        var wav = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
        _ = File.Exists(wav)
            ? PlaySoundW(wav, 0, SndFilename | SndAsync | SndNodefault)
            : PlaySoundW("SystemExclamation", 0, SndAsync);
    }

    /// <summary>Last notification problem, for troubleshooting (kept in the app's own data folder).</summary>
    private static void Log(string message)
    {
        try
        {
            File.WriteAllText(Path.Combine(TimerEngine.DataFolder(), "alarm.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Nothing else to do.
        }
    }

    [LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string? sound, nint module, uint flags);
}
