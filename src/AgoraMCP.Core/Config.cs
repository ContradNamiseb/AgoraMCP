namespace AgoraMCP;

/// <summary>
/// Where the shared blackboard lives. Every agent on the machine must resolve
/// the SAME file for the hub to exist at all, so the precedence is fixed and
/// documented: --db flag, then AGORA_DB, then one canonical path under
/// LOCALAPPDATA.
/// </summary>
public static class Config
{
    public const string EnvVar = "AGORA_DB";

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgoraMCP", "agora.db");

    public static string ResolveDbPath(string? explicitPath = null)
    {
        var path = FirstNonEmpty(explicitPath, Environment.GetEnvironmentVariable(EnvVar)) ?? DefaultPath;
        return Path.GetFullPath(path);
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
