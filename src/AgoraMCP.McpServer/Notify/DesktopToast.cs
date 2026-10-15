using DesktopNotifications;
#if WINDOWS
using DesktopNotifications.Windows;
#else
using DesktopNotifications.FreeDesktop;
#endif

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Agora thread 19/38 (user-directed, "Agora Signal Path" plan), Tier 1:
/// native OS toast notifications, fired directly from NotifyHub's own pump
/// loop the moment a new directed message lands. This is deliberately the
/// layer that doesn't care which agent CLI is on the receiving end -- it
/// notifies the human, not a specific tool's undocumented internals, so it
/// is the one tier that already covers every agent on both of the user's
/// machines.
///
/// <para>No shell exec anywhere: the Windows leg talks to the native WinRT
/// toast API in-process, and the Linux leg talks to
/// org.freedesktop.Notifications directly over the session D-Bus -- neither
/// shells out to a CLI tool (no PowerShell/BurntToast, no notify-send).</para>
///
/// <para><c>#if WINDOWS</c> rather than a runtime <c>RuntimeInformation</c>
/// check: the project file (see its own comment) resolves TargetFramework
/// per build OS -- Windows_NT builds net10.0-windows10.0.19041.0 and
/// references DesktopNotifications.Windows, everything else builds plain
/// net10.0 and references DesktopNotifications.FreeDesktop only. Only one
/// of those two packages is ever actually present in a given build, so this
/// has to be a compile-time branch, not a runtime one -- a runtime check on
/// the Linux leg would try to construct a WindowsNotificationManager type
/// that was never referenced at all and fail to compile, not just to run.
/// WINDOWS is the .NET SDK's own auto-defined symbol for an OS-versioned
/// TFM, not a symbol this project invented.</para>
///
/// <para>Fails soft by design: a machine with no notification server
/// running (a bare Linux console with no session bus, a locked-down Windows
/// build) must never take the hub down with it. <see cref="TryCreateAsync"/>
/// swallows initialization failures and returns null; <see cref="ShowAsync"/>
/// swallows per-notification failures. Either failure is logged once to
/// stderr, not on every subsequent message. Root-caused live, not guessed:
/// WindowsNotificationManager.Initialize() threw TypeLoadException on plain
/// net10.0 (no Windows TFM suffix) before the project-file fix above --
/// this class's own try/catch could not have masked that, since the throw
/// happened at construction, which is why both construction and Initialize
/// share one try block below rather than construction sitting outside it.</para>
/// </summary>
public sealed class DesktopToast : IAsyncDisposable
{
    private readonly INotificationManager _manager;
    private bool _warnedOnFailure;

    private DesktopToast(INotificationManager manager)
    {
        _manager = manager;
    }

    /// <summary>
    /// Construct and initialize the platform-appropriate manager. Returns
    /// null (never throws) if this platform's notification plumbing isn't
    /// available -- the hub runs identically either way, just without
    /// toasts.
    /// </summary>
    public static async Task<DesktopToast?> TryCreateAsync()
    {
        // Construction and Initialize() are BOTH inside this one try: see
        // the class comment above for why that matters here specifically.
        INotificationManager manager;
        try
        {
#if WINDOWS
            manager = new WindowsNotificationManager();
#else
            manager = new FreeDesktopNotificationManager();
#endif
            await manager.Initialize();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"hub: desktop notifications unavailable ({e.GetType().Name}: {e.Message}); continuing without toasts.");
            return null;
        }
        return new DesktopToast(manager);
    }

    /// <summary>
    /// Show one toast. Never throws -- a transient notification-server
    /// hiccup (session bus restart, explorer.exe crash) must not take the
    /// hub or its SSE fan-out down with it.
    /// </summary>
    public async Task ShowAsync(string title, string body)
    {
        try
        {
            await _manager.ShowNotification(new Notification { Title = title, Body = body });
        }
        catch (Exception e)
        {
            if (!_warnedOnFailure)
            {
                Console.Error.WriteLine($"hub: toast delivery failed ({e.Message}); will keep trying silently.");
                _warnedOnFailure = true;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        (_manager as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
