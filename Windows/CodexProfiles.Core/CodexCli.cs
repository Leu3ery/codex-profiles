using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProfiles.Core;

public sealed class CodexRpcException(bool requiresSignIn) : Exception(requiresSignIn
    ? "This session requires a new sign-in. Automatic retries are paused."
    : "Codex could not complete the request. Check your connection and Codex CLI version.")
{
    public bool RequiresSignIn { get; } = requiresSignIn;
}

public sealed class CodexCli(string executable, ProfileStore store)
{
    public static string Find(string? configured = null)
    {
        var explicitPath = configured ?? Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) && Path.GetExtension(explicitPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(explicitPath) : throw new IOException("Select a native codex.exe, not an npm .cmd wrapper.");
        var roots = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Directory.Exists).ToList();
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var direct = Path.Combine(root, "codex.exe");
            if (File.Exists(direct)) return direct;
            var packages = Path.Combine(root, "node_modules", "@openai");
            if (!Directory.Exists(packages)) continue;
            try
            {
                foreach (var package in Directory.EnumerateDirectories(packages, "codex*"))
                {
                    var native = Directory.EnumerateFiles(package, "codex.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(x => x.Contains(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                            ? "aarch64" : "x86_64", StringComparison.OrdinalIgnoreCase));
                    if (native is not null) return native;
                }
            }
            catch (UnauthorizedAccessException) { }
        }
        throw new IOException("Codex CLI not found. Install it with npm install -g @openai/codex, then reopen this app, or select codex.exe in Settings.");
    }

    private Process Start(string home, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = home,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["CODEX_HOME"] = home;
        foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" }) info.Environment.Remove(key);
        return Process.Start(info) ?? throw new IOException("Could not start Codex CLI.");
    }

    private static void Stop(Process process) { if (!process.HasExited) process.Kill(entireProcessTree: true); }

    public async Task LoginAsync(Action<string> onUrl, CancellationToken cancellationToken)
    {
        using var workspace = new PrivateWorkspace(store.Root);
        using var process = Start(workspace.Path, "login", "-c", "cli_auth_credentials_store=\"file\"");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var found = 0;
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"https://auth\.openai\.com/[^\s\x1b]+");
                if (match.Success && Interlocked.Exchange(ref found, 1) == 0) onUrl(match.Value);
            }
        }
        try
        {
            await Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError), process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new IOException("Sign-in failed. Try again using the browser link.");
            var auth = Path.Combine(workspace.Path, "auth.json");
            if (!File.Exists(auth)) throw new IOException("Codex did not save auth.json after signing in.");
            store.Import(await File.ReadAllTextAsync(auth, deadline.Token));
        }
        finally { Stop(process); await process.WaitForExitAsync(CancellationToken.None); }
    }

    private async Task<JsonObject> RequestAsync(string home, (string Method, object Params)[] requests,
        bool experimental, CancellationToken cancellationToken, bool verifyRenewal = false)
    {
        var queued = requests.ToList();
        var previous = verifyRenewal ? new AuthDocument(File.ReadAllText(Path.Combine(home, "auth.json"))) : null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        using var process = Start(home, "app-server", "-c", "cli_auth_credentials_store=\"file\"", "--listen", "stdio://");
        // Drain diagnostics without retaining or displaying credentials from the child process.
        var stderr = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (await process.StandardError.ReadAsync(buffer, deadline.Token) > 0) { }
        }, CancellationToken.None);
        async Task Send(object request) => await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
        var total = 0;
        async Task<JsonObject> Receive(int expected)
        {
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                total += line.Length;
                if (total > 1_048_576) throw new IOException("Codex response exceeded the limit.");
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
                if (AuthDocument.Text(message?["method"]) == "account/chatgptAuthTokens/refresh")
                    throw new IOException("The access token expired. Renew this saved session or sign in again.");
                if (message?["id"] is not JsonValue id || !id.TryGetValue<int>(out var value) || value != expected) continue;
                if (message["error"] is { } error)
                {
                    var details = error.ToJsonString().ToLowerInvariant();
                    throw new CodexRpcException(new[] { "refresh_token_expired", "refresh_token_reused", "refresh_token_invalidated",
                        "refresh token has expired", "refresh token was already used", "refresh token was revoked" }.Any(details.Contains));
                }
                return message["result"] as JsonObject ?? throw new IOException("Unexpected Codex response.");
            }
            throw new IOException("Codex exited before completing the request.");
        }
        try
        {
            await Send(new { id = 0, method = "initialize", @params = new {
                clientInfo = new { name = "codex_profiles", title = "Codex Profiles", version = "2.0.3" },
                capabilities = new { experimentalApi = experimental } } });
            await Receive(0);
            await Send(new { method = "initialized" });
            JsonObject result = [];
            for (var i = 0; i < queued.Count; i++)
            {
                await Send(new { id = i + 1, method = queued[i].Method, @params = queued[i].Params });
                var response = await Receive(i + 1);
                if (queued[i].Method == "getAuthStatus")
                {
                    if (AuthDocument.Text(response["authMethod"]) == "chatgpt" && response["authToken"] is null)
                        throw new CodexRpcException(true);
                }
                else result = response;
                if (previous is not null && i == 0)
                {
                    var latest = new AuthDocument(File.ReadAllText(Path.Combine(home, "auth.json")));
                    // The legacy status endpoint observes refresh failures cached in this process.
                    if (latest.AccessToken == previous.AccessToken && latest.RefreshToken == previous.RefreshToken)
                        queued.Add(("getAuthStatus", new { includeToken = true, refreshToken = false }));
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Codex timed out. Any rotated credentials will be preserved."); }
        finally
        {
            Stop(process);
            deadline.Cancel();
            await process.WaitForExitAsync(CancellationToken.None);
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }

    public async Task<UsageSnapshot> LimitsAsync(Guid id, CancellationToken cancellationToken)
    {
        store.Synchronize();
        var auth = store.Auth(id);
        if (auth.ExpiresAt is null || auth.ExpiresAt <= DateTimeOffset.UtcNow || auth.AccessToken is null || auth.AccountId is null)
            throw new InvalidOperationException("Renew this session or sign in again before checking limits. API key profiles do not expose ChatGPT limits.");
        using var workspace = new PrivateWorkspace(store.Root);
        var result = await RequestAsync(workspace.Path, [
            ("account/login/start", new { type = "chatgptAuthTokens", accessToken = auth.AccessToken, chatgptAccountId = auth.AccountId }),
            ("account/rateLimits/read", new { })], true, cancellationToken);
        return UsageSnapshot.Parse(result);
    }

    public async Task RenewAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!store.CanRenew(id)) throw new InvalidOperationException("Only inactive, independently signed-in sessions can be renewed. Codex manages the active session.");
        var previous = store.Auth(id);
        using var workspace = new PrivateWorkspace(store.Root);
        var path = Path.Combine(workspace.Path, "auth.json");
        Storage.WriteText(path, previous.Json);
        Exception? failure = null;
        try
        {
            await RequestAsync(workspace.Path, [("account/read", new { refreshToken = true })], false, cancellationToken, verifyRenewal: true);
        }
        catch (Exception e) { failure = e; }
        try { store.CompleteRenewal(id, new AuthDocument(File.ReadAllText(path)), previous); }
        catch (Exception e) { failure ??= e; }
        var profile = store.Profiles.Single(x => x.Id == id);
        store.Update(profile with { LastRenewalAttemptAt = DateTimeOffset.UtcNow,
            RenewalRequiresSignIn = failure is CodexRpcException { RequiresSignIn: true },
            RenewalStatus = failure?.Message ?? "Session renewed. Subscription dates are unchanged." });
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
