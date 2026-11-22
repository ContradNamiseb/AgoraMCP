using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgoraMCP.Tests;

/// <summary>
/// The freshly built MCP server apphost, for tests that must drive the real
/// executable rather than call Cli.Run in-process (the missing-dispatch class
/// of gap those tests exist for).
///
/// The TFM is derived from where the TESTS are running, not hardcoded: the
/// McpServer csproj picks a Windows-versioned TFM on Windows, and the test
/// project mirrors that rule, so the two move together. Hardcoding "net10.0"
/// is what went wrong before — on Windows the server stopped writing that
/// directory, the File.Exists assertion still found the OLD binary from an
/// earlier build, and four reachability tests kept passing against a stale
/// executable (found 2026-09-18 while adding the write-tool tests).
/// </summary>
internal static class BuiltApphost
{
    public static string Path
    {
        get
        {
            // AppContext.BaseDirectory ends with a separator: bin/Release/<tfm>/
            var tfm = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar)).Name;
            var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var exeName = OperatingSystem.IsWindows() ? "agora.exe" : "agora";
            var exe = System.IO.Path.Combine(repoRoot, "src", "AgoraMCP.McpServer",
                "bin", "Release", tfm, exeName);
            Assert.True(File.Exists(exe),
                $"expected the built apphost at {exe} (build the solution in Release first)");
            return exe;
        }
    }
}

/// <summary>
/// Minimal MCP client over stdio: spawns `agora serve --db ...` and speaks
/// JSON-RPC, so the tests exercise the SAME binding path a real client uses.
/// That matters for the write tools specifically — the failures of 2026-09-18
/// happened in the SDK's argument binding, before any tool code ran, and no
/// in-process test can see that layer.
/// </summary>
internal sealed class McpStdioClient : IDisposable
{
    private readonly Process _process;
    private int _nextId = 1;
    // Kept (not just drained): a server that dies mid-test explains itself
    // through stderr, and the failure message is the only place left to read.
    private readonly List<string> _stderr = new();
    private string StderrTail => _stderr.Count == 0
        ? "(no stderr)" : string.Join(" | ", _stderr.TakeLast(5));

    private string Diagnostics =>
        $"server {(_process.HasExited ? $"exited code={_process.ExitCode}" : "still running")}, stderr: {StderrTail}";

    public JsonElement InitializeResult { get; }

    public McpStdioClient(string dbPath, string? agentEnv = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BuiltApphost.Path,
            Arguments = $"serve --db \"{dbPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment.Remove("AGORA_AGENT");
        if (agentEnv is not null) psi.Environment["AGORA_AGENT"] = agentEnv;

        _process = Process.Start(psi)!;
        _ = Task.Run(() =>
        {
            try
            {
                // Drain to keep the pipe from blocking; keep the tail.
                string? line;
                while ((line = _process.StandardError.ReadLine()) is not null)
                    lock (_stderr) _stderr.Add(line);
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        });

        Send(new { jsonrpc = "2.0", id = _nextId++, method = "initialize", @params = new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "agora-tests", version = "0" },
        } });
        InitializeResult = ReadResult(_nextId - 1);

        // A notification (no id) — no response is sent for it.
        _process.StandardInput.WriteLine(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
        _process.StandardInput.Flush();
    }

    /// <summary>tools/call with the given arguments; returns (isError, text).</summary>
    public (bool IsError, string Text) CallTool(string name, object arguments)
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } });
        var result = ReadResult(id);

        var text = "";
        if (result.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
            text = content[0].GetProperty("text").GetString() ?? "";
        var isError = result.TryGetProperty("isError", out var err) && err.GetBoolean();
        return (isError, text);
    }

    /// <summary>
    /// The LIVE tool list, as a real client sees it — the true registration
    /// guard: reflecting over [McpServerToolType] classes sees declared tools
    /// that StdioServer never registered (that exact gap shipped the DM and
    /// chat tools unregistered on 2026-09-30).
    /// </summary>
    public IReadOnlyList<string> ListTools()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "tools/list", @params = new { } });
        return ReadResult(id).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? "").ToList();
    }

    /// <summary>The live tools/list entries in full: name, description and input schema.</summary>
    public IReadOnlyList<JsonElement> ListToolSchemas()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "tools/list", @params = new { } });
        return ReadResult(id).GetProperty("tools").EnumerateArray().Select(t => t.Clone()).ToList();
    }

    /// <summary>The URIs the server offers as resource templates.</summary>
    public IReadOnlyList<string> ListResourceTemplates()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "resources/templates/list", @params = new { } });
        return ReadResult(id).GetProperty("resourceTemplates").EnumerateArray()
            .Select(t => t.GetProperty("uriTemplate").GetString() ?? "").ToList();
    }

    /// <summary>The URIs the server offers as static resources.</summary>
    public IReadOnlyList<string> ListResources()
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "resources/list", @params = new { } });
        return ReadResult(id).GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("uri").GetString() ?? "").ToList();
    }

    /// <summary>resources/read; returns the first text content.</summary>
    public string ReadResource(string uri)
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "resources/read", @params = new { uri } });
        var contents = ReadResult(id).GetProperty("contents");
        Assert.True(contents.GetArrayLength() > 0, "resources/read returned no contents");
        return contents[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>resources/subscribe; the registry gates updated notifications.</summary>
    public void Subscribe(string uri)
    {
        var id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method = "resources/subscribe", @params = new { uri } });
        ReadResult(id);
    }

    /// <summary>
    /// Waits up to <paramref name="seconds"/> for a notification with this
    /// method; skips requests' responses and unrelated notifications. Fails
    /// the test on timeout, carrying the server's stderr and exit code.
    /// </summary>
    public JsonElement WaitForNotification(string method, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var line = ReadLineBounded(deadline,
                $"no '{method}' notification within {seconds}s; {Diagnostics}");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("method", out var got)) continue;
            if (got.GetString() != method) continue;
            return doc.RootElement.Clone();
        }
    }

    /// <summary>True when no notification of this method arrives within the window.</summary>
    public bool NoNotification(string method, double seconds)
    {
        // A blocking ReadLine cannot honor the deadline itself (a quiet server
        // would block past it), so the read runs on a task and only its
        // completion is bounded.
        var read = Task.Run<bool>(() =>
        {
            while (true)
            {
                var line = _process.StandardOutput.ReadLine();
                if (line is null) return false;  // stdout closed: nothing more can arrive
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("method", out var got) &&
                        got.GetString() == method) return true;
                }
                catch (JsonException) { }
            }
        });
        return !read.Wait(TimeSpan.FromSeconds(seconds)) || !read.Result;
    }

    private void Send(object message)
    {
        _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
        _process.StandardInput.Flush();
    }

    private JsonElement ReadResult(int id)
    {
        // Bounded read: a wrong id or a crashed server must fail the test, not
        // hang the suite. The deadline has to bound the ReadLine itself —
        // checking it only between lines hangs forever on a quiet server.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var line = ReadLineBounded(deadline,
                $"no response for request {id} within 30s; {Diagnostics}");
            if (string.IsNullOrWhiteSpace(line)) continue;

            var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("id", out var gotId)) continue;
            if (gotId.GetInt32() != id) continue;

            Assert.False(doc.RootElement.TryGetProperty("error", out var rpcError),
                $"json-rpc error: {rpcError}; {Diagnostics}");
            return doc.RootElement.GetProperty("result").Clone();
        }
    }

    /// <summary>
    /// Reads one stdout line, but never past `deadline`: a server that stays
    /// silent (or died) fails with `message` instead of blocking forever.
    /// A null line means stdout closed — surfaced, not spun on.
    /// </summary>
    private string? ReadLineBounded(DateTime deadline, string message)
    {
        var remaining = deadline - DateTime.UtcNow;
        var read = _process.StandardOutput.ReadLineAsync();
        if (!read.Wait(remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining))
            throw new Xunit.Sdk.XunitException(message);
        Assert.True(read.Result is not null, $"the mcp server closed stdout; {Diagnostics}");
        return read.Result;
    }

    public void Dispose()
    {
        try { _process.StandardInput.Close(); } catch (InvalidOperationException) { }
        if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
        _process.Dispose();
    }
}
