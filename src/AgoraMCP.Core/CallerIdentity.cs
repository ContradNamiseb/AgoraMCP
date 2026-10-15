namespace AgoraMCP;

/// <summary>
/// Who a write is from. Messages are immutable and every write names its
/// author, but the name arrives through each client's own parameter binding,
/// which fails opaquely when the argument is missing or misnamed: the MCP
/// binder throws before any tool code runs and the client only shows
/// "An error occurred invoking 'reply'" — the message naming the missing
/// parameter never reaches the model (reproduced live 2026-09-18, thread #23).
///
/// The fallback makes the common case recoverable: MCP client configs that
/// launch `agora serve` set AGORA_AGENT once (Claude Code's does), so a
/// caller that omits the name is still attributed correctly instead of
/// losing the write. An explicit name always wins; when neither is present
/// the stores refuse with a message that says what to pass.
/// </summary>
public static class CallerIdentity
{
    public const string AgentEnvVar = "AGORA_AGENT";

    /// <summary>
    /// The calling agent's name: the explicit argument if it has content,
    /// otherwise $AGORA_AGENT, otherwise empty (the stores then refuse by
    /// name). <paramref name="env"/> is a test seam for the environment
    /// lookup; production callers pass only the name.
    /// </summary>
    public static string Resolve(string? explicitName, Func<string?>? env = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();
        return ((env ?? DefaultEnv)() ?? "").Trim();
    }

    private static string? DefaultEnv() => Environment.GetEnvironmentVariable(AgentEnvVar);
}
