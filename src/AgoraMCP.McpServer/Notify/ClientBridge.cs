using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Hub-less, zero-CPU idle-wake dispatcher (`agora bridge`): waits on its own
/// ephemeral <see cref="NamedBoardEvent"/> and delivers incoming peer DMs and
/// directed board messages into sanctioned client-native local APIs:
///
/// <list type="bullet">
///   <item><c>codex</c>: <c>codex queue --thread &lt;id&gt; --message &lt;text&gt;</c> (Codex CLI 0.153+)</item>
///   <item><c>kimi-web</c>: <c>POST /api/v1/sessions/{id}/prompts</c> on local <c>kimi web</c> (Bearer token)</item>
///   <item><c>opencode</c>: <c>opencode api POST /session/{id}/prompt_async</c> or direct HTTP when <c>--url</c> is set</item>
/// </list>
///
/// <para>Active session/thread IDs are discovered from <c>--session</c>, from the
/// beacon file written by <c>agora hook-check --record-session</c>
/// (<c>%LOCALAPPDATA%\AgoraMCP\sessions\&lt;agent&gt;.json</c>), or via client-specific
/// read-only discovery (Codex latest session rollout UUID / Kimi Web <c>GET /api/v1/sessions</c>).</para>
/// </summary>
public static partial class ClientBridge
{
    public const string DefaultKimiWebUrl = "http://127.0.0.1:58627";

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant)]
    private static partial Regex UuidRegex();

    public sealed record Options
    {
        public required string DbPath { get; init; }
        /// <summary>Target client adapter: codex | kimi-web | opencode.</summary>
        public required string Client { get; init; }
        /// <summary>Agora board identity (defaults to codex / kimicode / opencode based on Client).</summary>
        public string? Agent { get; init; }
        /// <summary>Explicit session or thread ID override.</summary>
        public string? SessionId { get; init; }
        /// <summary>Optional active-session beacon JSON path (defaults to HookCheck.DefaultSessionPath).</summary>
        public string? SessionFilePath { get; init; }
        /// <summary>Optional HTTP server URL (kimi-web / opencode).</summary>
        public string? ServerUrl { get; init; }
        /// <summary>Optional Bearer token override (kimi-web) or Basic password (opencode).</summary>
        public string? AuthToken { get; init; }
        /// <summary>Optional CLI executable path override (codex / opencode).</summary>
        public string? ExecutablePath { get; init; }
        /// <summary>Optional Kimi home directory override (for testing; defaults to ~/.kimi-code).</summary>
        public string? KimiHomeDir { get; init; }
        /// <summary>Optional Codex home directory override (for testing; defaults to ~/.codex).</summary>
        public string? CodexHomeDir { get; init; }
        public long? Since { get; init; }
        public long? SinceTurn { get; init; }
        public bool IncludeAll { get; init; }
        public bool DmsOnly { get; init; }
        public bool Once { get; init; }
        public bool DryRun { get; init; }
        public double? TimeoutSeconds { get; init; }
        public double IntervalSeconds { get; init; } = NamedBoardEvent.IsSupported ? 5 : 1;
        /// <summary>Per-thread sliding-window wake cap (0 = unlimited). Prevents agent ping-pong loops.</summary>
        public int MaxWakesPerThread { get; init; } = 6;
        /// <summary>Sliding window duration in seconds for <see cref="MaxWakesPerThread"/>.</summary>
        public double WakeWindowSeconds { get; init; } = 600;
    }

    public sealed record DispatchRequest(
        string Client,
        string Agent,
        string SessionId,
        string Message,
        IReadOnlyList<BoardEvent> Events);

    public sealed record DispatchResult(bool Ok, string Detail);

    public static string NormalizeClient(string? client) =>
        (client ?? "").Trim().ToLowerInvariant() switch
        {
            "codex" => "codex",
            "kimi" or "kimi-web" or "kimicode" or "kimi_web" => "kimi-web",
            "opencode" or "open-code" => "opencode",
            var other => other,
        };

    public static string DefaultAgentForClient(string normalizedClient) =>
        normalizedClient switch
        {
            "codex" => "codex",
            "kimi-web" => "kimicode",
            "opencode" => "opencode",
            _ => normalizedClient,
        };

    public static async Task<int> RunAsync(
        Options options,
        TextWriter? log = null,
        CancellationToken token = default,
        Func<DispatchRequest, CancellationToken, Task<DispatchResult>>? dispatcher = null)
    {
        var err = log ?? Console.Error;
        var client = NormalizeClient(options.Client);
        if (client is not ("codex" or "kimi-web" or "opencode"))
        {
            err.WriteLine($"bridge: unsupported --client '{options.Client}'. Expected: codex, kimi-web, or opencode.");
            return 2;
        }

        var agent = string.IsNullOrWhiteSpace(options.Agent)
            ? DefaultAgentForClient(client)
            : options.Agent.Trim();

        var dbPath = Path.GetFullPath(options.DbPath);
        var listenerId = $"bridge-{client}-{HookCheck.SanitizeFileSegment(agent)}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
        EventWaitHandle? ownEvent = null;

        try
        {
            ownEvent = NamedBoardEvent.OpenOwnEvent(dbPath, listenerId);
            if (ownEvent is not null)
                NamedBoardEvent.Register(dbPath, listenerId);
        }
        catch
        {
            ownEvent?.Dispose();
            ownEvent = null;
        }

        try
        {
            var cursor = options.Since ?? NotifyHub.ReadMaxSeq(dbPath);
            var turnCursor = options.SinceTurn
                ?? (options.Since == 0 ? 0 : NotifyHub.ReadMaxTurnSeq(dbPath));
            var deadline = options.TimeoutSeconds is > 0
                ? DateTime.UtcNow.AddSeconds(options.TimeoutSeconds.Value)
                : (DateTime?)null;
            var fallback = TimeSpan.FromSeconds(Math.Clamp(options.IntervalSeconds, 0.1, 60));
            var wakeHistory = new Dictionary<long, Queue<DateTime>>();

            err.WriteLine($"bridge: watching '{agent}' for client '{client}' (cursor={cursor}, turn={turnCursor}, db={dbPath}{(options.DryRun ? ", dry-run" : "")})");
            err.Flush();

            var firstPass = true;
            while (!token.IsCancellationRequested)
            {
                if (!firstPass)
                {
                    var wait = fallback;
                    if (deadline is { } dl)
                    {
                        var remaining = dl - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) return 0;
                        if (remaining < wait) wait = remaining;
                    }
                    try
                    {
                        await ChannelBridge.WaitForWakeAsync(ownEvent, wait, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
                firstPass = false;

                try
                {
                    var (events, maxSeq, maxTurnSeq) = NotifyHub.ReadCombinedSince(
                        dbPath,
                        cursor,
                        turnCursor,
                        agent,
                        directedOnly: !options.IncludeAll,
                        dmsOnly: options.DmsOnly,
                        excludeSelf: true);

                    if (maxSeq > cursor) cursor = maxSeq;
                    if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;

                    if (events.Count > 0)
                    {
                        var deliverable = FilterByWakeCap(events, wakeHistory, options.MaxWakesPerThread,
                            TimeSpan.FromSeconds(Math.Max(1, options.WakeWindowSeconds)), err, client);
                        if (deliverable.Count == 0)
                        {
                            if (options.Once) return 0;
                            continue;
                        }

                        var message = HookCheck.FormatEvents(deliverable);
                        var sessionId = await ResolveTargetSessionAsync(client, agent, options, token);
                        var req = new DispatchRequest(client, agent, sessionId ?? "", message, deliverable);

                        DispatchResult res;
                        if (options.DryRun)
                        {
                            res = new DispatchResult(true,
                                $"dry-run: would deliver {deliverable.Count} event(s) to {client} session '{req.SessionId}'");
                        }
                        else if (dispatcher is not null)
                        {
                            res = await dispatcher(req, token);
                        }
                        else
                        {
                            res = await DispatchAsync(req, options, token);
                        }

                        err.WriteLine($"bridge [{client}]: {(res.Ok ? "ok" : "warn")} — {res.Detail}");
                        err.Flush();

                        if (options.Once) return res.Ok ? 0 : 1;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    err.WriteLine($"bridge [{client}]: error ({e.Message}) — continuing.");
                    err.Flush();
                }

                if (deadline is { } dlAfter && DateTime.UtcNow >= dlAfter)
                    return 0;
            }
            return 0;
        }
        finally
        {
            if (ownEvent is not null)
            {
                NamedBoardEvent.Unregister(dbPath, listenerId);
                ownEvent.Dispose();
            }
        }
    }

    public static async Task<string?> ResolveTargetSessionAsync(
        string normalizedClient, string agent, Options options, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(options.SessionId))
            return options.SessionId.Trim();

        var sessionFile = string.IsNullOrWhiteSpace(options.SessionFilePath)
            ? HookCheck.DefaultSessionPath(agent)
            : options.SessionFilePath!;
        var beacon = HookCheck.LoadSessionBeacon(sessionFile);
        if (beacon is not null && !string.IsNullOrWhiteSpace(beacon.SessionId))
            return beacon.SessionId.Trim();

        return normalizedClient switch
        {
            "codex" => DiscoverLatestCodexSessionId(options.CodexHomeDir),
            "kimi-web" => await DiscoverLatestKimiWebSessionAsync(options, token),
            _ => null,
        };
    }

    public static async Task<DispatchResult> DispatchAsync(
        DispatchRequest request, Options options, CancellationToken token) =>
        request.Client switch
        {
            "codex" => await DispatchCodexAsync(request, options, token),
            "kimi-web" => await DispatchKimiWebAsync(request, options, token),
            "opencode" => await DispatchOpenCodeAsync(request, options, token),
            _ => new DispatchResult(false, $"unsupported client '{request.Client}'"),
        };

    public static IReadOnlyList<string> BuildCodexQueueArgs(string threadId, string message) =>
        ["queue", "--thread", threadId, "--message", message];

    internal static async Task<DispatchResult> DispatchCodexAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return new DispatchResult(false,
                "no active Codex thread ID found (pass --session <UUID>, or install Codex hooks with --record-session)");
        }

        var exe = string.IsNullOrWhiteSpace(options.ExecutablePath) ? "codex" : options.ExecutablePath.Trim();
        var args = BuildCodexQueueArgs(request.SessionId, request.Message);
        var (exitCode, stdout, stderr) = await RunSubprocessAsync(exe, args, TimeSpan.FromSeconds(30), token);
        if (exitCode == 0)
            return new DispatchResult(true, $"queued {request.Events.Count} event(s) to codex thread {request.SessionId}");

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return new DispatchResult(false,
            $"codex queue failed (exit {exitCode}) for thread {request.SessionId}: {Clip(detail, 240)}");
    }

    internal static async Task<DispatchResult> DispatchKimiWebAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        var baseUrl = ResolveKimiWebBaseUrl(options);
        var bearer = ResolveKimiWebToken(options);
        var sessionId = request.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = await DiscoverLatestKimiWebSessionAsync(options, token) ?? "";

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new DispatchResult(false,
                $"no active Kimi Web session found at {baseUrl} (start `kimi web` and open a session, or pass --session)");
        }

        var endpoint = $"{baseUrl.TrimEnd('/')}/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/prompts";
        var payload = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = request.Message } },
        });

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(bearer))
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var resp = await SharedHttp.SendAsync(httpReq, token);
        if (resp.IsSuccessStatusCode)
            return new DispatchResult(true, $"posted {request.Events.Count} event(s) to kimi-web session {sessionId} ({(int)resp.StatusCode})");

        var body = await resp.Content.ReadAsStringAsync(token);
        return new DispatchResult(false,
            $"kimi-web POST {endpoint} returned {(int)resp.StatusCode}: {Clip(body, 240)}");
    }

    internal static async Task<DispatchResult> DispatchOpenCodeAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
        {
            return new DispatchResult(false,
                "no active OpenCode session ID found (prefer the native plugins/opencode-agora-notify plugin, or pass --session)");
        }

        var jsonBody = JsonSerializer.Serialize(new
        {
            parts = new[] { new { type = "text", text = request.Message } },
        });

        // Direct HTTP when --url is given (works cleanly without Windows .cmd quoting issues).
        if (!string.IsNullOrWhiteSpace(options.ServerUrl))
        {
            var endpoint = $"{options.ServerUrl.Trim().TrimEnd('/')}/session/{Uri.EscapeDataString(request.SessionId)}/prompt_async";
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(options.AuthToken))
            {
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{options.AuthToken.Trim()}"));
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            using var resp = await SharedHttp.SendAsync(httpReq, token);
            if (resp.IsSuccessStatusCode)
                return new DispatchResult(true, $"posted {request.Events.Count} event(s) to opencode session {request.SessionId} ({(int)resp.StatusCode})");
            var body = await resp.Content.ReadAsStringAsync(token);
            return new DispatchResult(false, $"opencode POST {endpoint} returned {(int)resp.StatusCode}: {Clip(body, 240)}");
        }

        // Otherwise invoke `opencode api POST /session/<id>/prompt_async --data <json>`.
        var exe = string.IsNullOrWhiteSpace(options.ExecutablePath) ? "opencode" : options.ExecutablePath.Trim();
        var args = new List<string> { "api", "POST", $"/session/{request.SessionId}/prompt_async", "--data", jsonBody };
        var (exitCode, stdout, stderr) = await RunSubprocessAsync(exe, args, TimeSpan.FromSeconds(30), token);
        if (exitCode == 0)
            return new DispatchResult(true, $"posted {request.Events.Count} event(s) via opencode api to session {request.SessionId}");

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return new DispatchResult(false, $"opencode api failed (exit {exitCode}): {Clip(detail, 240)}");
    }

    public static string ResolveKimiWebBaseUrl(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerUrl))
            return options.ServerUrl.Trim().TrimEnd('/');

        var kimiHome = ResolveKimiHome(options.KimiHomeDir);
        var instancesDir = Path.Combine(kimiHome, "server", "instances");
        try
        {
            if (Directory.Exists(instancesDir))
            {
                var newest = new DirectoryInfo(instancesDir)
                    .EnumerateFiles("*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(newest.FullName, Encoding.UTF8));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                    {
                        var url = urlProp.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                            return url.Trim().TrimEnd('/');
                    }
                    if (root.TryGetProperty("port", out var portProp) && portProp.TryGetInt32(out var port) && port > 0)
                    {
                        var host = root.TryGetProperty("host", out var hostProp) && hostProp.ValueKind == JsonValueKind.String
                            ? (hostProp.GetString() ?? "127.0.0.1")
                            : "127.0.0.1";
                        if (host is "0.0.0.0" or "::" or "") host = "127.0.0.1";
                        return $"http://{host}:{port}";
                    }
                }
            }
        }
        catch
        {
            // Fall through to default loopback URL.
        }

        return DefaultKimiWebUrl;
    }

    public static string? ResolveKimiWebToken(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            return options.AuthToken.Trim();

        var envToken = Environment.GetEnvironmentVariable("KIMI_SERVER_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return envToken.Trim();

        var tokenFile = Path.Combine(ResolveKimiHome(options.KimiHomeDir), "server.token");
        try
        {
            if (File.Exists(tokenFile))
            {
                var text = File.ReadAllText(tokenFile, Encoding.UTF8).Trim();
                if (text.Length > 0) return text;
            }
        }
        catch
        {
            // Ignore unreadable token file.
        }
        return null;
    }

    internal static async Task<string?> DiscoverLatestKimiWebSessionAsync(Options options, CancellationToken token)
    {
        var baseUrl = ResolveKimiWebBaseUrl(options);
        var bearer = ResolveKimiWebToken(options);
        var endpoint = $"{baseUrl.TrimEnd('/')}/api/v1/sessions";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            if (!string.IsNullOrWhiteSpace(bearer))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await SharedHttp.SendAsync(req, token);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement list = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("sessions", out var s)) list = s;
                else if (root.TryGetProperty("items", out var items)) list = items;
                else if (root.TryGetProperty("data", out var data)) list = data;
            }
            if (list.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "session_id", "sessionId", "id" })
                {
                    if (item.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
                    }
                }
            }
        }
        catch
        {
            // Kimi web server may not be running right now.
        }
        return null;
    }

    internal static string? DiscoverLatestCodexSessionId(string? codexHomeDir)
    {
        var home = string.IsNullOrWhiteSpace(codexHomeDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : codexHomeDir!;
        var sessionsDir = Path.Combine(home, "sessions");
        try
        {
            if (!Directory.Exists(sessionsDir)) return null;
            var newest = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null) return null;
            var match = UuidRegex().Match(newest.Name);
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveKimiHome(string? overrideDir) =>
        string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kimi-code")
            : overrideDir!;

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunSubprocessAsync(
        string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{fileName}'");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static List<BoardEvent> FilterByWakeCap(
        List<BoardEvent> events,
        Dictionary<long, Queue<DateTime>> history,
        int maxWakes,
        TimeSpan window,
        TextWriter err,
        string client)
    {
        if (maxWakes <= 0) return events;
        var now = DateTime.UtcNow;
        var cutoff = now - window;
        var allowed = new List<BoardEvent>(events.Count);
        var countedThreads = new HashSet<long>();
        foreach (var ev in events)
        {
            if (!history.TryGetValue(ev.ThreadId, out var q))
            {
                q = new Queue<DateTime>();
                history[ev.ThreadId] = q;
            }
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();

            if (!countedThreads.Contains(ev.ThreadId))
            {
                if (q.Count >= maxWakes)
                {
                    err.WriteLine(
                        $"bridge [{client}]: wake cap ({maxWakes}/{window.TotalMinutes:0.#}m) reached for thread #{ev.ThreadId}; leaving msg #{ev.MsgId} in pending queries.");
                    err.Flush();
                    continue;
                }
                q.Enqueue(now);
                countedThreads.Add(ev.ThreadId);
            }
            allowed.Add(ev);
        }
        return allowed;
    }

    private static string Clip(string text, int max)
    {
        var cleaned = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "…";
    }
}
