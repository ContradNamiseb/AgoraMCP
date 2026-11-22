using System.Globalization;
using AgoraMCP.Database;
using AgoraMCP.McpServer.Watch;

namespace AgoraMCP.McpServer;

/// <summary>
/// Entry point. One executable serves both roles: the operational verbs used
/// from a terminal, and 'serve', which an MCP client launches to speak
/// JSON-RPC over stdio. 'watch' is the plan-v1 resident: it sleeps on the
/// board's named event and spawns a peer turn when a peer posts.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return (args.FirstOrDefault() ?? "help") switch
            {
                "serve" => StdioServer.RunAsync(Option(args, "--db")).GetAwaiter().GetResult(),
                "watch" => Watch(Option(args, "--db"), Option(args, "--agent"),
                    Option(args, "--exec"), Option(args, "--cwd"), Option(args, "--state"),
                    Option(args, "--heartbeat"), Option(args, "--log-dir"),
                    double.TryParse(Option(args, "--interval"), out var s) ? s : 60,
                    double.TryParse(Option(args, "--timeout"), out var t) ? t : 900,
                    int.TryParse(Option(args, "--max-attempts"), out var m) ? m : 3,
                    double.TryParse(Option(args, "--backoff"), out var b) ? b : 60),
                "chat" => ChatView.Run(
                    Option(args, "--db") ?? Config.ResolveDbPath(),
                    double.TryParse(Option(args, "--interval"), out var ci) ? ci : 2,
                    // Thread #21 review #124, finding 2: no hardcoded agent.
                    // --agent wins, then the watcher contract's AGORA_AGENT;
                    // neither set = no-agent view with neutral directed labels.
                    Option(args, "--agent")
                        ?? Environment.GetEnvironmentVariable("AGORA_AGENT")
                        ?? ""),
                "ack" => Cli.Run(args),
                "pending" => Cli.Run(args),
                "review" or "revise" => Cli.Run(args),
                "handoff" => Cli.Run(args),
                "brief" => Cli.Run(args),
                // Token-saving verbs: the same routing lesson three times over
                // (pending, review/revise, brief) — a command Cli.Run handles but
                // Program.cs never dispatches fails as "unknown command".
                "digest" or "note" or "resume" or "memo" or "ask" or "memos" or "tokens" => Cli.Run(args),
                "list" or "read" or "search" or "stats" or "online" or "turn" => Cli.Run(args),
                "hub" => Hub(Option(args, "--db"), Option(args, "--port")),
                "subscribe" => SubscribeCmd(Option(args, "--agent"), Option(args, "--hub"), Option(args, "--since")),
                "tail" => TailCmd(args),
                "hook-check" => HookCheckCmd(args),
                "bridge" => BridgeCmd(args),
                "doctor" => Notify.Doctor.Run(Option(args, "--db") is { Length: > 0 } ddb ? ddb : Config.ResolveDbPath()),
                "help" or "--help" or "-h" => Help(0),
                var unknown => Fail($"unknown command '{unknown}'"),
            };
        }
        catch (AgoraException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Watch(string? db, string? agent, string? exec, string? cwd,
        string? state, string? heartbeat, string? logDir, double interval,
        double timeout, int maxAttempts, double backoff)
    {
        if (string.IsNullOrWhiteSpace(agent))
        {
            Console.Error.WriteLine("watch: --agent is required (the identity this watch serves).");
            return 2;
        }
        db = string.IsNullOrWhiteSpace(db) ? Config.ResolveDbPath() : db;
        agent = agent.Trim();
        return WatchRunner.Run(new WatchRunner.Options
        {
            DbPath = db,
            Agent = agent,
            Exec = string.IsNullOrWhiteSpace(exec) ? null : exec,
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd,
            StatePath = state ?? WatchRunner.DefaultStatePath(agent),
            HeartbeatPath = heartbeat ?? Path.Combine(
                Path.GetDirectoryName(state ?? WatchRunner.DefaultStatePath(agent)) ?? ".",
                $"watch-heartbeat-{agent}.stamp"),
            LogDirectory = logDir ?? Path.Combine(
                Path.GetDirectoryName(state ?? WatchRunner.DefaultStatePath(agent)) ?? ".", "watch-logs"),
            CoarseWaitSeconds = interval,
            TurnTimeoutSeconds = timeout,
            MaxTurnAttempts = maxAttempts,
            RetryBackoffSeconds = backoff,
        });
    }

    private static int Hub(string? db, string? port)
    {
        // Resident push fan-out (no scripts, no broker): loopback SSE over
        // the SQLite log. Ctrl+C stops it; subscribers resume via ?since=.
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Notify.NotifyHub.RunAsync(new Notify.NotifyHub.Options
        {
            DbPath = string.IsNullOrWhiteSpace(db) ? Config.ResolveDbPath() : db,
            Port = int.TryParse(port, out var p) ? p : Notify.NotifyHub.DefaultPort,
        }, cts.Token).GetAwaiter().GetResult();
    }

    private static int SubscribeCmd(string? agent, string? hub, string? since)
    {
        // Agent sidecar: prints one event JSON object per line until killed.
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Notify.Subscribe.RunAsync(new Notify.Subscribe.Options
        {
            Hub = string.IsNullOrWhiteSpace(hub) ? $"127.0.0.1:{Notify.NotifyHub.DefaultPort}" : hub.Trim(),
            Agent = agent ?? "",
            Since = long.TryParse(since, out var s) ? s : 0,
        }, cts.Token).GetAwaiter().GetResult();
    }

    private static int TailCmd(string[] args)
    {
        var agent = Option(args, "--agent") ?? Environment.GetEnvironmentVariable("AGORA_AGENT") ?? "";
        var db = Option(args, "--db");
        if (!TryParseOptionalLong(args, "--since", "tail", out var since)) return 2;
        if (!TryParseOptionalLong(args, "--since-turn", "tail", out var sinceTurn)) return 2;
        if (!TryParseOptionalDouble(args, "--timeout", "tail", out var timeout)) return 2;
        if (!TryParseOptionalDouble(args, "--interval", "tail", out var interval)) return 2;
        if (!TryParseOptionalInt(args, "--parent-pid", "tail", out var parentPid)) return 2;

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Notify.BoardTail.RunAsync(new Notify.BoardTail.Options
        {
            DbPath = string.IsNullOrWhiteSpace(db) ? Config.ResolveDbPath() : db,
            Agent = agent,
            Since = since,
            SinceTurn = sinceTurn,
            IncludeAll = args.Contains("--all"),
            DmsOnly = args.Contains("--dms-only"),
            Once = args.Contains("--once"),
            TimeoutSeconds = timeout,
            IntervalSeconds = interval ?? (NamedBoardEvent.IsSupported ? 5 : 1),
            ParentPid = parentPid,
            ExitWithParent = args.Contains("--exit-with-parent"),
            ExitOnStdinEof = args.Contains("--exit-on-stdin-eof"),
            EmitReady = args.Contains("--emit-ready"),
        }, stdout: null, token: cts.Token).GetAwaiter().GetResult();
    }

    private static int HookCheckCmd(string[] args)
    {
        var agent = Option(args, "--agent") ?? Environment.GetEnvironmentVariable("AGORA_AGENT") ?? "";
        var db = Option(args, "--db");
        // Fail-silent even on bad flags (L15): exit code 2 means "block stop" in
        // Claude, Codex, and Kimi Stop hooks, so a config typo must never exit 2.
        if (!TryParseOptionalLong(args, "--since", "hook-check", out var since)) return 0;
        if (!TryParseOptionalInt(args, "--max-blocks", "hook-check", out var maxBlocks)) return 0;

        return Notify.HookCheck.Run(new Notify.HookCheck.Options
        {
            DbPath = string.IsNullOrWhiteSpace(db) ? Config.ResolveDbPath() : db,
            Agent = agent,
            Format = Option(args, "--format") ?? "standard",
            EventName = Option(args, "--event") ?? "auto",
            StatePath = Option(args, "--state"),
            RecordSession = args.Contains("--record-session"),
            SessionFilePath = Option(args, "--session-file"),
            Since = since,
            IncludeAll = args.Contains("--all"),
            DmsOnly = args.Contains("--dms-only"),
            NoAdvance = args.Contains("--no-advance"),
            MaxBlocks = maxBlocks ?? Notify.HookCheck.DefaultMaxBlocks,
        });
    }

    private static int BridgeCmd(string[] args)
    {
        var client = Option(args, "--client") ?? "";
        var agent = Option(args, "--agent");
        var db = Option(args, "--db");
        if (!TryParseOptionalLong(args, "--since", "bridge", out var since)) return 2;
        if (!TryParseOptionalLong(args, "--since-turn", "bridge", out var sinceTurn)) return 2;
        if (!TryParseOptionalDouble(args, "--timeout", "bridge", out var timeout)) return 2;
        if (!TryParseOptionalDouble(args, "--interval", "bridge", out var interval)) return 2;
        if (!TryParseOptionalInt(args, "--max-wakes", "bridge", out var maxWakes)) return 2;
        if (!TryParseOptionalInt(args, "--max-global-wakes", "bridge", out var maxGlobalWakes)) return 2;
        if (!TryParseOptionalInt(args, "--parent-pid", "bridge", out var parentPid)) return 2;
        if (!TryParseOptionalNonNegativeDouble(args, "--session-max-age-sec", "bridge", out var sessionMaxAge)) return 2;

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return Notify.ClientBridge.RunAsync(new Notify.ClientBridge.Options
        {
            DbPath = string.IsNullOrWhiteSpace(db) ? Config.ResolveDbPath() : db,
            Client = client,
            Agent = agent,
            SessionId = Option(args, "--session"),
            SessionFilePath = Option(args, "--session-file"),
            SessionMaxAgeSeconds = sessionMaxAge ?? Notify.ClientBridge.DefaultSessionMaxAgeSeconds,
            ServerUrl = Option(args, "--url"),
            AuthToken = Option(args, "--token"),
            AuthTokenFile = Option(args, "--token-file"),
            ExecutablePath = Option(args, "--exe"),
            Since = since,
            SinceTurn = sinceTurn,
            IncludeAll = args.Contains("--all"),
            DmsOnly = args.Contains("--dms-only"),
            Once = args.Contains("--once"),
            DryRun = args.Contains("--dry-run"),
            TimeoutSeconds = timeout,
            IntervalSeconds = interval ?? (NamedBoardEvent.IsSupported ? 5 : 1),
            MaxWakesPerThread = maxWakes ?? 6,
            MaxGlobalWakes = maxGlobalWakes ?? Notify.ClientBridge.DefaultMaxGlobalWakes,
            ParentPid = parentPid,
            ExitWithParent = args.Contains("--exit-with-parent"),
            ExitOnStdinEof = args.Contains("--exit-on-stdin-eof"),
        }, log: null, token: cts.Token).GetAwaiter().GetResult();
    }

    private static bool TryParseOptionalLong(string[] args, string flag, string cmd, out long? value)
    {
        value = null;
        var raw = Option(args, flag);
        if (raw is null) return true;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        Console.Error.WriteLine($"{cmd}: invalid {flag} '{raw}' (expected non-negative integer).");
        return false;
    }

    private static bool TryParseOptionalInt(string[] args, string flag, string cmd, out int? value)
    {
        value = null;
        var raw = Option(args, flag);
        if (raw is null) return true;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        Console.Error.WriteLine($"{cmd}: invalid {flag} '{raw}' (expected non-negative integer).");
        return false;
    }

    private static bool TryParseOptionalDouble(string[] args, string flag, string cmd, out double? value)
    {
        value = null;
        var raw = Option(args, flag);
        if (raw is null) return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed > 0)
        {
            value = parsed;
            return true;
        }
        Console.Error.WriteLine($"{cmd}: invalid {flag} '{raw}' (expected positive number, e.g. 0.5 or 5).");
        return false;
    }

    private static bool TryParseOptionalNonNegativeDouble(string[] args, string flag, string cmd, out double? value)
    {
        value = null;
        var raw = Option(args, flag);
        if (raw is null) return true;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }
        Console.Error.WriteLine($"{cmd}: invalid {flag} '{raw}' (expected non-negative number, e.g. 0 or 14400).");
        return false;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return Help(2);
    }

    private static int Help(int exitCode)
    {
        Console.WriteLine("agora — cross-agent collaboration hub (shared blackboard over MCP + SQLite)");
        Console.WriteLine(Cli.HelpText);
        return exitCode;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
