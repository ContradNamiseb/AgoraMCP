using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AgoraMCP.McpServer;

// Closed vocabularies as enums, so the tool schema carries an "enum" list and
// a client can only offer valid values. The wire names are the strings the
// stores already persist, so rows written before this change still match.

[JsonConverter(typeof(JsonStringEnumConverter<ReviewVerdict>))]
public enum ReviewVerdict
{
    [JsonStringEnumMemberName("approved")] Approved,
    [JsonStringEnumMemberName("changes_required")] ChangesRequired,
    [JsonStringEnumMemberName("rejected")] Rejected,
    [JsonStringEnumMemberName("awaiting_review")] AwaitingReview,
}

[JsonConverter(typeof(JsonStringEnumConverter<TurnRole>))]
public enum TurnRole
{
    [JsonStringEnumMemberName("implementer")] Implementer,
    [JsonStringEnumMemberName("reviewer")] Reviewer,
    [JsonStringEnumMemberName("tester")] Tester,
    [JsonStringEnumMemberName("researcher")] Researcher,
}

[JsonConverter(typeof(JsonStringEnumConverter<TurnStage>))]
public enum TurnStage
{
    [JsonStringEnumMemberName("in_progress")] InProgress,
    [JsonStringEnumMemberName("dev_tested")] DevTested,
    [JsonStringEnumMemberName("reviewing")] Reviewing,
    [JsonStringEnumMemberName("review_approved")] ReviewApproved,
    [JsonStringEnumMemberName("changes_requested")] ChangesRequested,
    [JsonStringEnumMemberName("done")] Done,
}

[JsonConverter(typeof(JsonStringEnumConverter<ThreadStatus>))]
public enum ThreadStatus
{
    [JsonStringEnumMemberName("open")] Open,
    [JsonStringEnumMemberName("answered")] Answered,
    [JsonStringEnumMemberName("closed")] Closed,
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageKind>))]
public enum MessageKind
{
    [JsonStringEnumMemberName("reply")] Reply,
    [JsonStringEnumMemberName("review")] Review,
}

[JsonConverter(typeof(JsonStringEnumConverter<TokenKind>))]
public enum TokenKind
{
    [JsonStringEnumMemberName("spend")] Spend,
    [JsonStringEnumMemberName("saved")] Saved,
}

[JsonConverter(typeof(JsonStringEnumConverter<TokenSource>))]
public enum TokenSource
{
    [JsonStringEnumMemberName("digest")] Digest,
    [JsonStringEnumMemberName("note")] Note,
    [JsonStringEnumMemberName("memo")] Memo,
    [JsonStringEnumMemberName("search")] Search,
    [JsonStringEnumMemberName("read")] Read,
    [JsonStringEnumMemberName("spawn")] Spawn,
    [JsonStringEnumMemberName("manual")] Manual,
}

/// <summary>The routing vocabulary every agent's instructions name; routing depends on it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Specialty>))]
public enum Specialty
{
    [JsonStringEnumMemberName("kernel")] Kernel,
    [JsonStringEnumMemberName("research")] Research,
    [JsonStringEnumMemberName("refactor")] Refactor,
    [JsonStringEnumMemberName("review")] Review,
    [JsonStringEnumMemberName("windows")] Windows,
}

[JsonConverter(typeof(JsonStringEnumConverter<ChatClient>))]
public enum ChatClient
{
    [JsonStringEnumMemberName("claudecode")] ClaudeCode,
    [JsonStringEnumMemberName("zcode")] ZCode,
    [JsonStringEnumMemberName("kimicode")] KimiCode,
    [JsonStringEnumMemberName("codex")] Codex,
    [JsonStringEnumMemberName("antigravity")] Antigravity,
    [JsonStringEnumMemberName("opencode")] OpenCode,
    [JsonStringEnumMemberName("github-copilot")] GitHubCopilot,
    [JsonStringEnumMemberName("vscode")] VsCode,
    [JsonStringEnumMemberName("cursor")] Cursor,
}

/// <summary>The persisted string for an enum value: its JSON member name.</summary>
public static class EnumWire
{
    private static readonly ConcurrentDictionary<Enum, string> Names = new();

    public static string Wire<T>(this T value) where T : struct, Enum =>
        Names.GetOrAdd(value, v => typeof(T).GetField(v.ToString())!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? v.ToString().ToLowerInvariant());

    public static string Wire<T>(this T? value) where T : struct, Enum =>
        value is { } v ? v.Wire() : "";

    /// <summary>Every wire name of <typeparamref name="T"/>, for error messages.</summary>
    public static string All<T>() where T : struct, Enum =>
        string.Join(", ", Enum.GetValues<T>().Select(v => v.Wire()));
}
