using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer.Notify;

/// <summary>
/// Hub-less, zero-CPU idle-wake dispatcher (`agora bridge`): waits on its own
/// ephemeral <see cref="NamedBoardEvent"/> and delivers incoming peer DMs,
/// directed board messages, and turn passes into sanctioned client-native local APIs:
///
/// <list type="bullet">
///   <item><c>codex</c>: <c>codex queue --thread &lt;id&gt; --message &lt;text&gt;</c> (native .exe only; refuses .cmd/.bat wrappers)</item>
///   <item><c>kimi-web</c>: <c>POST /api/v1/sessions/{id}/prompts</c> on loopback <c>kimi web</c> (Bearer token, no redirects)</item>
///   <item><c>opencode</c>: <c>POST /session/{id}/prompt_async</c> on loopback HTTP (default http://127.0.0.1:4096 + service.json Basic auth, no redirects)</item>
/// </list>
///
/// <para>Requires either an explicit <c>--session</c> or a fresh active-session
/// beacon written by <c>agora hook-check --record-session</c>; never guesses an
/// unrelated session, refuses non-loopback HTTP targets (M2), validates session
/// IDs against leading-dash flag injection (L6), and enforces both per-thread
/// and global latching loop breakers (I4).</para>
/// </summary>
public static class ClientBridge
{
    public const string DefaultKimiWebUrl = "http://127.0.0.1:58627";
    public const string DefaultOpenCodeUrl = "http://127.0.0.1:4096";
    public const double DefaultSessionMaxAgeSeconds = 4 * 60 * 60; // 4 hours
    public const double DefaultWakeCooldownSeconds = 60 * 60; // 60 min loop-breaker latch
    public const int DefaultMaxGlobalWakes = 15;

    // M2: never follow HTTP redirects with Authorization headers attached.
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

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
        /// <summary>Reject beacon files older than this many seconds (0 = no expiry; default 4h).</summary>
        public double SessionMaxAgeSeconds { get; init; } = DefaultSessionMaxAgeSeconds;
        /// <summary>Optional loopback HTTP server URL (kimi-web / opencode).</summary>
        public string? ServerUrl { get; init; }
        /// <summary>Optional Bearer token / Basic password override (prefer --token-file or env var to avoid argv exposure).</summary>
        public string? AuthToken { get; init; }
        /// <summary>Optional path to a file containing the Bearer token / Basic password (M4).</summary>
        public string? AuthTokenFile { get; init; }
        /// <summary>Optional CLI executable path override (codex; .cmd/.bat wrappers are refused).</summary>
        public string? ExecutablePath { get; init; }
        /// <summary>Optional Kimi home directory override (for testing; defaults to ~/.kimi-code).</summary>
        public string? KimiHomeDir { get; init; }
        /// <summary>Optional OpenCode config directory override (for testing; defaults to ~/.config/opencode).</summary>
        public string? OpenCodeConfigDir { get; init; }
        public long? Since { get; init; }
        public long? SinceTurn { get; init; }
        public bool IncludeAll { get; init; }
        public bool DmsOnly { get; init; }
        public bool Once { get; init; }
        public bool DryRun { get; init; }
        public double? TimeoutSeconds { get; init; }
        public double IntervalSeconds { get; init; } = NamedBoardEvent.IsSupported ? 5 : 1;
        /// <summary>Per-thread wake cap before latching into cooldown (0 = unlimited).</summary>
        public int MaxWakesPerThread { get; init; } = 6;
        /// <summary>Global wake cap across all threads before latching into cooldown (0 = unlimited, I4).</summary>
        public int MaxGlobalWakes { get; init; } = DefaultMaxGlobalWakes;
        /// <summary>Window duration in seconds for counting wakes toward caps.</summary>
        public double WakeWindowSeconds { get; init; } = 600;
        /// <summary>Latch duration in seconds once a thread or global cap trips.</summary>
        public double WakeCooldownSeconds { get; init; } = DefaultWakeCooldownSeconds;
        /// <summary>Exit cleanly if this parent PID exits.</summary>
        public int? ParentPid { get; init; }
        /// <summary>Auto-detect own parent PID on Windows/Linux/macOS and exit cleanly if it terminates.</summary>
        public bool ExitWithParent { get; init; }
        /// <summary>Exit cleanly when stdin reaches EOF.</summary>
        public bool ExitOnStdinEof { get; init; }
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

    /// <summary>
    /// Validates a session/thread ID against empty values, whitespace/control chars,
    /// and leading dashes (L6: prevents a crafted session_id from being parsed as
    /// a CLI option such as --dangerously-bypass-approvals-and-sandbox).
    /// </summary>
    public static bool IsSafeSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var s = sessionId.Trim();
        if (s.Length > 200 || s.StartsWith('-')) return false;
        foreach (var ch in s)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// Enforces loopback-only HTTP/HTTPS targets before attaching credentials (M2).
    /// </summary>
    public static (string? NormalizedUrl, string? Error) ValidateLoopbackUrl(string rawUrl)
    {
        var trimmed = (rawUrl ?? "").Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return (null, $"invalid server URL '{rawUrl}'");
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"refusing unsupported URL scheme '{uri.Scheme}' in '{rawUrl}'");
        }
        if (!NotifyHub.IsLoopbackHostname(uri.Host))
        {
            return (null,
                $"refusing non-loopback target '{uri.Host}' in '{rawUrl}' (credentials are only sent to loopback 127.0.0.1/localhost/::1)");
        }
        return (trimmed.TrimEnd('/'), null);
    }

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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var linkedToken = linkedCts.Token;

        Process? watchedParent = null;
        var targetParentPid = options.ParentPid
            ?? (options.ExitWithParent ? BoardTail.TryGetOwnParentPid() : null);

        if (targetParentPid is > 0)
        {
            if (!BoardTail.TryAttachParentWatcher(targetParentPid.Value, linkedCts, out watchedParent))
                return 0;
        }
        if (options.ExitOnStdinEof)
        {
            _ = BoardTail.WatchStdinEofAsync(linkedCts);
        }

        var dbPath = Path.GetFullPath(options.DbPath);
        var listenerId = $"bridge-{client}-{HookCheck.AgentFileKey(agent)}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";
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
            var latchedUntil = new Dictionary<long, DateTime>();
            var globalHistory = new Queue<DateTime>();
            DateTime? globalLatchedUntil = null;

            err.WriteLine($"bridge: watching '{agent}' for client '{client}' (cursor={cursor}, turn={turnCursor}, db={dbPath}{(options.DryRun ? ", dry-run" : "")})");
            err.Flush();
            NotifyLog.Write($"bridge:{client}", agent, "started", cursor, turnCursor);

            var firstPass = true;
            while (!linkedToken.IsCancellationRequested)
            {
                if (!firstPass)
                {
                    if (watchedParent is not null && BoardTail.SafeHasExited(watchedParent))
                        return 0;

                    var wait = fallback;
                    if (deadline is { } dl)
                    {
                        var remaining = dl - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) return 0;
                        if (remaining < wait) wait = remaining;
                    }
                    try
                    {
                        await ChannelBridge.WaitForWakeAsync(ownEvent, wait, linkedToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
                firstPass = false;

                if (watchedParent is not null && BoardTail.SafeHasExited(watchedParent))
                    return 0;

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

                    if (events.Count == 0)
                    {
                        if (maxSeq > cursor) cursor = maxSeq;
                        if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;
                        continue;
                    }

                    var window = TimeSpan.FromSeconds(Math.Max(1, options.WakeWindowSeconds));
                    var cooldown = TimeSpan.FromSeconds(Math.Max(1, options.WakeCooldownSeconds));

                    // Global loop breaker (I4)
                    if (options.MaxGlobalWakes > 0 && globalLatchedUntil is DateTime gUntil)
                    {
                        if (DateTime.UtcNow < gUntil)
                        {
                            var remMin = Math.Max(1, (int)Math.Ceiling((gUntil - DateTime.UtcNow).TotalMinutes));
                            err.WriteLine($"bridge [{client}]: global wake cap cooldown active ({remMin}m left); leaving {events.Count} event(s) in pending queries.");
                            err.Flush();
                            NotifyLog.Write($"bridge:{client}", agent, $"global cooldown active ({remMin}m left)", maxSeq, maxTurnSeq);
                            if (maxSeq > cursor) cursor = maxSeq;
                            if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;
                            if (options.Once) return 0;
                            continue;
                        }
                        globalLatchedUntil = null;
                    }

                    var deliverable = FilterByWakeCap(
                        events, wakeHistory, latchedUntil, options.MaxWakesPerThread, window, cooldown, err, client, agent);
                    if (deliverable.Count == 0)
                    {
                        if (maxSeq > cursor) cursor = maxSeq;
                        if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;
                        if (options.Once) return 0;
                        continue;
                    }

                    var message = HookCheck.FormatEvents(deliverable);
                    var (sessionId, sessionError) = ResolveTargetSession(agent, options);
                    if (string.IsNullOrWhiteSpace(sessionId) && !options.DryRun && dispatcher is null)
                    {
                        err.WriteLine($"bridge [{client}]: skipped — {sessionError}; mail stays in pending queries.");
                        err.Flush();
                        NotifyLog.Write($"bridge:{client}", agent, $"skipped: {sessionError}", maxSeq, maxTurnSeq);
                        if (maxSeq > cursor) cursor = maxSeq;
                        if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;
                        if (options.Once) return 1;
                        continue;
                    }

                    var req = new DispatchRequest(client, agent, sessionId ?? "", message, deliverable);

                    // I1: dispatch BEFORE advancing cursors; retry once on transient failure in resident mode.
                    DispatchResult res;
                    if (options.DryRun)
                    {
                        var detail = string.IsNullOrWhiteSpace(req.SessionId)
                            ? $"dry-run: {sessionError ?? "no session"}; would leave {deliverable.Count} event(s) in pending queries"
                            : $"dry-run: would deliver {deliverable.Count} event(s) to {client} session '{req.SessionId}'";
                        res = new DispatchResult(true, detail);
                    }
                    else
                    {
                        Func<Task<DispatchResult>> runOnce = dispatcher is not null
                            ? () => dispatcher(req, linkedToken)
                            : () => DispatchAsync(req, options, linkedToken);

                        res = await runOnce();
                        if (!res.Ok && !options.Once && dispatcher is null && !linkedToken.IsCancellationRequested)
                        {
                            try { await Task.Delay(500, linkedToken); }
                            catch (OperationCanceledException) { return 0; }
                            res = await runOnce();
                        }
                    }

                    if (res.Ok)
                    {
                        RecordSuccessfulWake(
                            deliverable, wakeHistory, latchedUntil, globalHistory, ref globalLatchedUntil,
                            options.MaxWakesPerThread, options.MaxGlobalWakes, window, cooldown);
                    }

                    if (maxSeq > cursor) cursor = maxSeq;
                    if (maxTurnSeq > turnCursor) turnCursor = maxTurnSeq;

                    err.WriteLine($"bridge [{client}]: {(res.Ok ? "ok" : "warn")} — {res.Detail}");
                    err.Flush();
                    NotifyLog.Write($"bridge:{client}", agent, $"{(res.Ok ? "ok" : "warn")}: {res.Detail}", cursor, turnCursor);

                    if (options.Once) return res.Ok ? 0 : 1;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    err.WriteLine($"bridge [{client}]: error ({e.Message}) — {(options.Once ? "aborting (--once)." : "continuing.")}");
                    err.Flush();
                    NotifyLog.Write($"bridge:{client}", agent, $"error: {e.Message}", cursor, turnCursor);
                    if (options.Once) return 1;
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
            watchedParent?.Dispose();
        }
    }

    /// <summary>
    /// Resolves the target session ONLY from an explicit <c>--session</c> or a
    /// fresh beacon file (<c>--session-file</c> / <see cref="HookCheck.DefaultSessionPath"/>).
    /// Rejects unsafe session IDs (e.g. starting with <c>-</c>) and missing/invalid
    /// <c>updated_at</c> when expiry is enabled (L6 / N9).
    /// </summary>
    public static (string? SessionId, string? Reason) ResolveTargetSession(string agent, Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            var explicitId = options.SessionId.Trim();
            return IsSafeSessionId(explicitId)
                ? (explicitId, null)
                : (null, $"invalid --session value '{explicitId}' (must not start with '-' or contain whitespace/control chars)");
        }

        var sessionFile = string.IsNullOrWhiteSpace(options.SessionFilePath)
            ? HookCheck.DefaultSessionPath(agent)
            : options.SessionFilePath!;
        if (!File.Exists(sessionFile))
        {
            return (null, $"no active session beacon at '{sessionFile}' (pass --session or enable --record-session hooks)");
        }

        var beacon = HookCheck.LoadSessionBeacon(sessionFile);
        if (beacon is null || !IsSafeSessionId(beacon.SessionId))
        {
            return (null, $"session beacon '{sessionFile}' has no valid session_id");
        }

        if (options.SessionMaxAgeSeconds > 0)
        {
            if (string.IsNullOrWhiteSpace(beacon.UpdatedAt) ||
                !DateTimeOffset.TryParse(beacon.UpdatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updatedAt))
            {
                return (null, $"session beacon '{sessionFile}' is missing a valid updated_at timestamp");
            }

            var age = DateTimeOffset.UtcNow - updatedAt;
            if (age.TotalSeconds > options.SessionMaxAgeSeconds)
            {
                return (null,
                    $"session beacon '{sessionFile}' is stale ({age.TotalMinutes:0}m old > {options.SessionMaxAgeSeconds / 60:0}m limit)");
            }
        }

        return (beacon.SessionId.Trim(), null);
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

    public static (string? ResolvedPath, string? Error) ResolveSafeExecutable(string requestedExe)
    {
        var exe = (requestedExe ?? "").Trim();
        if (exe.Length == 0)
            return (null, "executable path is empty");

        var normalizedForExt = exe.TrimEnd('.', ' ');
        var ext = Path.GetExtension(normalizedForExt);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                $"refusing batch wrapper '{exe}' (.cmd/.bat executes through cmd.exe and is unsafe for untrusted peer text); " +
                "pass the native .exe path via --exe or use HTTP --url.");
        }

        var hasSeparator = exe.Contains(Path.DirectorySeparatorChar) || exe.Contains(Path.AltDirectorySeparatorChar);
        if (hasSeparator)
        {
            if (!File.Exists(exe))
                return (null, $"executable not found: '{exe}'");
            return (exe, null);
        }

        if (!OperatingSystem.IsWindows())
            return (exe, null);

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sawBatchShim = (string?)null;
        var hasExeExt = ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            try
            {
                var exeCandidate = Path.Combine(dir, hasExeExt ? exe : exe + ".exe");
                if (File.Exists(exeCandidate))
                    return (exeCandidate, null);

                if (!hasExeExt && sawBatchShim is null)
                {
                    var cmdCandidate = Path.Combine(dir, exe + ".cmd");
                    if (File.Exists(cmdCandidate)) sawBatchShim = cmdCandidate;
                    else
                    {
                        var batCandidate = Path.Combine(dir, exe + ".bat");
                        if (File.Exists(batCandidate)) sawBatchShim = batCandidate;
                    }
                }
            }
            catch { }
        }

        if (sawBatchShim is not null)
        {
            return (null,
                $"'{exe}' on PATH resolves only to batch wrapper '{sawBatchShim}'; refusing .cmd/.bat execution — " +
                "pass the native .exe via --exe (or use HTTP --url).");
        }

        return (null, $"native executable '{exe}.exe' not found on PATH");
    }

    internal static async Task<DispatchResult> DispatchCodexAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        if (!IsSafeSessionId(request.SessionId))
        {
            return new DispatchResult(false,
                "invalid or missing Codex thread ID (pass --session <UUID>, or install Codex hooks with --record-session)");
        }

        var requestedExe = string.IsNullOrWhiteSpace(options.ExecutablePath) ? "codex" : options.ExecutablePath.Trim();
        var (safeExe, resolveErr) = ResolveSafeExecutable(requestedExe);
        if (safeExe is null)
            return new DispatchResult(false, resolveErr ?? "unable to resolve native codex executable");

        var args = BuildCodexQueueArgs(request.SessionId, request.Message);
        var (exitCode, stdout, stderr) = await RunSubprocessAsync(safeExe, args, TimeSpan.FromSeconds(30), token);
        if (exitCode == 0)
            return new DispatchResult(true, $"queued {request.Events.Count} event(s) to codex thread {request.SessionId}");

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return new DispatchResult(false,
            $"codex queue failed (exit {exitCode}) for thread {request.SessionId}: {Clip(detail, 240)}");
    }

    internal static async Task<DispatchResult> DispatchKimiWebAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        if (!IsSafeSessionId(request.SessionId))
        {
            return new DispatchResult(false,
                "invalid or missing Kimi Web session ID (pass --session <id> or enable Kimi hooks with --record-session)");
        }

        var rawUrl = ResolveKimiWebBaseUrl(options);
        var (baseUrl, urlErr) = ValidateLoopbackUrl(rawUrl);
        if (baseUrl is null)
            return new DispatchResult(false, urlErr!);

        var bearer = ResolveKimiWebToken(options);

        var endpoint = $"{baseUrl}/api/v1/sessions/{Uri.EscapeDataString(request.SessionId)}/prompts";
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
            return new DispatchResult(true, $"posted {request.Events.Count} event(s) to kimi-web session {request.SessionId} ({(int)resp.StatusCode})");

        var body = await resp.Content.ReadAsStringAsync(token);
        return new DispatchResult(false,
            $"kimi-web POST {endpoint} returned {(int)resp.StatusCode}: {Clip(body, 240)}");
    }

    internal static async Task<DispatchResult> DispatchOpenCodeAsync(
        DispatchRequest request, Options options, CancellationToken token)
    {
        if (!IsSafeSessionId(request.SessionId))
        {
            return new DispatchResult(false,
                "invalid or missing OpenCode session ID (prefer the native plugins/opencode-agora-notify plugin, or pass --session)");
        }

        var rawUrl = string.IsNullOrWhiteSpace(options.ServerUrl)
            ? DefaultOpenCodeUrl
            : options.ServerUrl.Trim();
        var (baseUrl, urlErr) = ValidateLoopbackUrl(rawUrl);
        if (baseUrl is null)
            return new DispatchResult(false, urlErr!);

        var password = ResolveOpenCodeServicePassword(options);

        var jsonBody = JsonSerializer.Serialize(new
        {
            parts = new[] { new { type = "text", text = request.Message } },
        });

        var endpoint = $"{baseUrl}/session/{Uri.EscapeDataString(request.SessionId)}/prompt_async";
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(password))
        {
            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}"));
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        }

        using var resp = await SharedHttp.SendAsync(httpReq, token);
        if (resp.IsSuccessStatusCode)
            return new DispatchResult(true, $"posted {request.Events.Count} event(s) to opencode session {request.SessionId} ({(int)resp.StatusCode})");

        var body = await resp.Content.ReadAsStringAsync(token);
        return new DispatchResult(false, $"opencode POST {endpoint} returned {(int)resp.StatusCode}: {Clip(body, 240)}");
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
        catch { }

        return DefaultKimiWebUrl;
    }

    private static string? ReadExplicitToken(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.AuthTokenFile))
        {
            try
            {
                if (File.Exists(options.AuthTokenFile))
                {
                    var t = File.ReadAllText(options.AuthTokenFile, Encoding.UTF8).Trim();
                    if (t.Length > 0) return t;
                }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(options.AuthToken))
            return options.AuthToken.Trim();
        return null;
    }

    public static string? ResolveKimiWebToken(Options options)
    {
        var explicitTok = ReadExplicitToken(options);
        if (explicitTok is not null) return explicitTok;

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
        catch { }
        return null;
    }

    public static string? ResolveOpenCodeServicePassword(Options options)
    {
        var explicitTok = ReadExplicitToken(options);
        if (explicitTok is not null) return explicitTok;

        var envPass = Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD");
        if (!string.IsNullOrWhiteSpace(envPass))
            return envPass.Trim();

        var ocDir = string.IsNullOrWhiteSpace(options.OpenCodeConfigDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode")
            : options.OpenCodeConfigDir!;
        var serviceFile = Path.Combine(ocDir, "service.json");
        try
        {
            if (File.Exists(serviceFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(serviceFile, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("password", out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var pw = prop.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(pw)) return pw;
                }
            }
        }
        catch { }
        return null;
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
        Dictionary<long, DateTime> latchedUntil,
        int maxWakes,
        TimeSpan window,
        TimeSpan cooldown,
        TextWriter err,
        string client,
        string agent)
    {
        if (maxWakes <= 0) return events;
        var now = DateTime.UtcNow;
        var cutoff = now - window;
        var allowed = new List<BoardEvent>(events.Count);
        var checkedThreads = new Dictionary<long, bool>();

        foreach (var ev in events)
        {
            if (checkedThreads.TryGetValue(ev.ThreadId, out var ok))
            {
                if (ok) allowed.Add(ev);
                continue;
            }

            if (latchedUntil.TryGetValue(ev.ThreadId, out var until))
            {
                if (now < until)
                {
                    var remMin = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
                    err.WriteLine(
                        $"bridge [{client}]: wake cap cooldown active ({remMin}m left) for thread #{ev.ThreadId}; leaving msg #{ev.MsgId} in pending queries.");
                    err.Flush();
                    NotifyLog.Write($"bridge:{client}", agent, $"thread #{ev.ThreadId} cooldown active ({remMin}m left)", ev.Seq, ev.TurnSeq);
                    checkedThreads[ev.ThreadId] = false;
                    continue;
                }
                latchedUntil.Remove(ev.ThreadId);
            }

            if (history.TryGetValue(ev.ThreadId, out var q))
            {
                while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
                if (q.Count >= maxWakes)
                {
                    latchedUntil[ev.ThreadId] = now + cooldown;
                    q.Clear();
                    err.WriteLine(
                        $"bridge [{client}]: wake cap ({maxWakes}/{window.TotalMinutes:0.#}m) tripped for thread #{ev.ThreadId}; latched for {cooldown.TotalMinutes:0}m, leaving msg #{ev.MsgId} in pending queries.");
                    err.Flush();
                    NotifyLog.Write($"bridge:{client}", agent, $"thread #{ev.ThreadId} wake cap tripped", ev.Seq, ev.TurnSeq);
                    checkedThreads[ev.ThreadId] = false;
                    continue;
                }
            }

            checkedThreads[ev.ThreadId] = true;
            allowed.Add(ev);
        }
        return allowed;
    }

    private static void RecordSuccessfulWake(
        List<BoardEvent> delivered,
        Dictionary<long, Queue<DateTime>> history,
        Dictionary<long, DateTime> latchedUntil,
        Queue<DateTime> globalHistory,
        ref DateTime? globalLatchedUntil,
        int maxWakes,
        int maxGlobalWakes,
        TimeSpan window,
        TimeSpan cooldown)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - window;

        if (maxGlobalWakes > 0)
        {
            while (globalHistory.Count > 0 && globalHistory.Peek() < cutoff) globalHistory.Dequeue();
            globalHistory.Enqueue(now);
            if (globalHistory.Count >= maxGlobalWakes)
            {
                globalLatchedUntil = now + cooldown;
                globalHistory.Clear();
            }
        }

        if (maxWakes <= 0) return;
        foreach (var tid in delivered.Select(e => e.ThreadId).Distinct())
        {
            if (!history.TryGetValue(tid, out var q))
            {
                q = new Queue<DateTime>();
                history[tid] = q;
            }
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            q.Enqueue(now);
            if (q.Count >= maxWakes)
            {
                latchedUntil[tid] = now + cooldown;
                q.Clear();
            }
        }
    }

    private static string Clip(string text, int max)
    {
        var cleaned = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= max ? cleaned : HookCheck.SafeSubstring(cleaned, max) + "…";
    }
}
