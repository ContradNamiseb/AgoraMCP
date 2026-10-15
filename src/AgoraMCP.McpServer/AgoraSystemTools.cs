using System.ComponentModel;
using ModelContextProtocol.Server;
using AgoraMCP.McpServer.SystemInterop;

namespace AgoraMCP.McpServer;

/// <summary>
/// Local OS situational-awareness tools (from the .NET MCP integration
/// architecture, PDF pp. 6-9, 12): what window is focused, what processes own
/// visible top-level windows. STRICTLY LOCAL telemetry — these tools exist so
/// an agent can answer "is the user looking at this" and "which agent session
/// owns that window" without anything leaving the machine. The cloud-mesh
/// half of the source architecture (Agora.io RTM relay, PingCloudAgent) is
/// deliberately NOT implemented: this board is local-only by user decision.
///
/// <para>Governance note: these two tools read desktop state (window titles
/// and executable paths) on behalf of ANY agent that can call tools. That is
/// consistent with the board's local-trust model, but it is also exactly the
/// surface the source architecture's policy layer (Microsoft.AgentGovernance,
/// default-deny tool allowlist) exists to gate — so "governance is future
/// hardening" understates it: this read surface is the concrete reason to add
/// it. Nothing here transmits; the risk is local over-exposure, not
/// exfiltration. Results are rendered as labelled text like every other
/// agora tool (read by a model, not a parser).</para>
/// </summary>
[McpServerToolType]
public sealed class AgoraSystemTools
{
    private AgoraSystemTools() { }

    [McpServerTool(Name = "get_active_application_state")]
    [Description("""
        Inspect the local operating system: which window is currently in the
        foreground, which process owns it (PID, name, executable path), and when
        the snapshot was taken. Local telemetry only — nothing is transmitted.
        Use it to check whether the user is actively looking at a given tool or
        editor before waiting on them, or to report situational context.
        """)]
    public static string GetActiveApplicationState() =>
        ToolResponses.RenderActiveWindow(OsContext.Service.GetActiveWindowDetails());

    [McpServerTool(Name = "list_top_level_windows")]
    [Description("""
        Enumerate all visible top-level windows on this machine with their owning
        process (PID, name, executable path). Local telemetry only. Use it to find
        which terminal or editor session owns a given window, or to get an overview
        of what is currently on screen.
        """)]
    public static string ListTopLevelWindows(
        [Description("Only return windows whose title contains this text (case-insensitive). Omit for all.")]
        string? title_filter = null)
    {
        var filter = title_filter?.Trim() ?? "";
        return ToolResponses.RenderWindowList(
            FilterByTitle(OsContext.Service.EnumerateTopLevelWindows(), filter), filter);
    }

    /// <summary>
    /// Case-insensitive title filter, kept pure so it can be unit-tested
    /// directly with synthetic windows instead of through a live enumeration
    /// (which is non-deterministic and may legitimately be empty on CI).
    /// </summary>
    internal static IReadOnlyList<ActiveWindowContext> FilterByTitle(
        IReadOnlyList<ActiveWindowContext> windows, string? titleFilter)
    {
        if (string.IsNullOrWhiteSpace(titleFilter)) return windows;
        var needle = titleFilter.Trim();
        return windows
            .Where(w => w.WindowTitle.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
