using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgoraMCP.McpServer.SystemInterop;

/// <summary>Situational awareness about one top-level window: which process owns it and what it is showing. Local-only telemetry — nothing here leaves the machine (thread #36-adjacent capability from the .NET MCP integration architecture, PDF pp. 6-9).</summary>
public sealed record ActiveWindowContext(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string ExecutablePath,
    DateTimeOffset Timestamp);

/// <summary>OS window telemetry: the foreground window and the visible top-level window list.</summary>
public interface IOsContextService
{
    ActiveWindowContext GetActiveWindowDetails();
    IReadOnlyList<ActiveWindowContext> EnumerateTopLevelWindows();
}

/// <summary>Windows implementation via User32. Non-Windows hosts get an emulated self-report so the MCP tools stay callable everywhere.</summary>
public sealed class WindowsContextService : IOsContextService
{
    public ActiveWindowContext GetActiveWindowDetails()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return EmulatedSelf();

        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return new ActiveWindowContext(0, "SystemIdle", "No Active Window",
                string.Empty, DateTimeOffset.UtcNow);
        }
        return ResolveContextFromHandle(hWnd);
    }

    public IReadOnlyList<ActiveWindowContext> EnumerateTopLevelWindows()
    {
        // Same contract as GetActiveWindowDetails on non-Windows hosts: one
        // emulated self-report, so the tool stays callable AND consistent. The
        // two methods used to disagree (one emulated, one returned nothing),
        // which made "empty list" ambiguous between "no windows" and "wrong OS".
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new[] { EmulatedSelf() };

        var results = new List<ActiveWindowContext>();
        EnumWindows((hWnd, _) =>
        {
            if (IsWindowVisible(hWnd) && GetWindowTextLength(hWnd) > 0)
                results.Add(ResolveContextFromHandle(hWnd));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    private static ActiveWindowContext EmulatedSelf()
    {
        var proc = Process.GetCurrentProcess();
        return new ActiveWindowContext(proc.Id, proc.ProcessName,
            "Non-Windows Host (Telemetry Emulated)",
            Environment.ProcessPath ?? "Unknown", DateTimeOffset.UtcNow);
    }

    private static ActiveWindowContext ResolveContextFromHandle(IntPtr hWnd)
    {
        var title = ReadWindowTitle(hWnd);

        GetWindowThreadProcessId(hWnd, out var processId);
        var processName = "Unknown";
        var executablePath = "Unknown";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            executablePath = process.MainModule?.FileName ?? "Access Denied";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or Win32Exception or NotSupportedException)
        {
            // Process gone, elevated, or a different bitness — title still useful.
        }

        return new ActiveWindowContext((int)processId, processName, title,
            executablePath, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reads a window caption. GetWindowTextLength is only an upper bound and
    /// the title can change between the two Win32 calls (TOCTOU), so if the
    /// copy fills the buffer, grow it and retry; and trust the returned
    /// character count rather than assuming the title always fit.
    /// </summary>
    private static string ReadWindowTitle(IntPtr hWnd)
    {
        for (var capacity = GetWindowTextLength(hWnd) + 1; capacity <= 4096; capacity *= 2)
        {
            var buffer = new StringBuilder(capacity);
            var copied = GetWindowText(hWnd, buffer, buffer.Capacity);
            if (copied < buffer.Capacity - 1) return buffer.ToString(0, copied);
        }
        return string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
}

/// <summary>Process-wide singleton: our MCP tools are static methods, so the service is reached statically rather than via DI.</summary>
public static class OsContext
{
    public static IOsContextService Service { get; } = new WindowsContextService();
}
