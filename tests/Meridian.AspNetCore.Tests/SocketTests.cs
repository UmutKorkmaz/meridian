using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
namespace Meridian.AspNetCore.Tests;

[Trait("Provider", "Redis")]
public sealed class SocketTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Actual_Sample_Process_Creates_Reads_Replays_And_Redacts(string environment)
    {
        await SocketHost.RunTest(false, environment, async host =>
        {
            using var client = host.Client();
            using var created = await Create(client); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var read = await client.GetAsync(created.Headers.Location); Assert.Equal(await created.Content.ReadAsStringAsync(), await read.Content.ReadAsStringAsync());
            using var replay = await Create(client); Assert.Equal(await created.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
            using var missing = host.Client(false); using var missingResponse = await missing.GetAsync("/orders"); await AssertProblem(missingResponse, 401, "authentication_required");
            using var denied = host.Client(permission: "none"); using var deniedResponse = await denied.GetAsync(created.Headers.Location); await AssertProblem(deniedResponse, 403, "authorization_denied");
            using var invalid = await Create(client, quantity: 0); await AssertProblem(invalid, 400, "validation_failed");
            using var malformed = await client.PostAsync("/orders", new StringContent("{secret", System.Text.Encoding.UTF8, "application/json")); await AssertProblem(malformed, 400, "invalid_request");
            using var cross = host.Client(tenant: "B"); using var crossResponse = await cross.GetAsync(created.Headers.Location); await AssertProblem(crossResponse, 404, "not_found");
            using var openapi = await client.GetAsync("/openapi/v1.json"); Assert.Contains("application/x-ndjson", await openapi.Content.ReadAsStringAsync());
            await using var db = host.Database(); Assert.Equal(1, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Orders)); Assert.Equal(1, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Events));
        });
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Real_Socket_Streaming_Disconnect_Joins_Cleanup_And_PostStart_Error_Truncates(string environment)
    {
        await SocketHost.RunTest(true, environment, async host =>
        {
            using var client = host.Client(); using var made = await Create(client);
            await host.Set("cleanup");
            using (var response = await SocketBody.Open(client, "/orders/events"))
            {
                Assert.Equal("application/x-ndjson", response.Response.Content.Headers.ContentType!.MediaType);
                Assert.Contains("book", await response.ReadLine());
            }
            await host.Until(x => x.GetProperty("cleanupEntered").GetBoolean());
            Assert.Equal(0, (await host.Counts()).GetProperty("streamDisposes").GetInt32());
            await host.Set("cleanup-release"); await host.Until(x => x.GetProperty("streamDisposes").GetInt32() == 1 && x.GetProperty("active").GetInt32() == 0);
            Assert.Equal(1, (await host.Counts()).GetProperty("streamCancellations").GetInt32());
            await host.Set("reset"); await host.Set("late");
            using var stream = await SocketBody.Open(client, "/orders/events"); Assert.Contains("book", await stream.ReadLine());
            var error = await Record.ExceptionAsync(async () => { var remainder = await stream.ReadToEnd(); Assert.DoesNotContain("problem", remainder); Assert.DoesNotContain("secret", remainder); });
            Assert.True(error is IOException or HttpRequestException, "The socket must report an aborted incomplete response.");
            await host.Until(x => x.GetProperty("streamDisposes").GetInt32() == 2 && x.GetProperty("active").GetInt32() == 0);
            await host.Set("reset"); await host.Set("initial"); using var initial = await client.GetAsync("/orders/events"); await AssertProblem(initial, 500, "internal_error");
        });
    }
    [Fact]
    public async Task Real_Socket_Faults_Inflight_Unknown_And_Rollback_Use_Real_Storage()
    {
        await SocketHost.RunTest(true, "Development", async host =>
        {
            using var client = host.Client();
            foreach (var (shape, status, code) in new[] { ("validation", 400, "validation_failed"), ("denied", 403, "authorization_denied"), ("missing", 500, "internal_error"), ("unknown", 409, "idempotency_outcome_unknown"), ("aggregate", 500, "internal_error"), ("other", 500, "internal_error") })
            { using var result = await client.GetAsync("/probe/fault/" + shape); await AssertProblem(result, status, code); }
            await host.Set("pause"); var first = Create(client, "inflight");
            try { await host.Until(x => x.GetProperty("entered").GetBoolean()); using var duplicate = await Create(client, "inflight"); await AssertProblem(duplicate, 409, "idempotency_in_progress"); }
            finally { await host.Set("release"); using var joined = await first; Assert.Equal(HttpStatusCode.Created, joined.StatusCode); }
            await host.Set("reset"); await host.Set("unexpected"); using var failure = await Create(client, "failure"); await AssertProblem(failure, 500, "internal_error");
            await host.Set("reset"); await host.Set("unknown"); using var uncertain = await Create(client, "uncertain"); await AssertProblem(uncertain, 409, "idempotency_outcome_unknown");
            using var second = await Create(client, "uncertain"); await AssertProblem(second, 409, "idempotency_in_progress");
            await using var db = host.Database(); Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Orders)); Assert.Equal(2, await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.Events));
            Assert.Equal(3, (await host.Counts()).GetProperty("handlerCalls").GetInt32());
        });
    }
    [Fact]
    public async Task Real_Socket_WarmCache_AuthCounts_And_Reconnected_Streams_Are_Scoped()
    {
        await SocketHost.RunTest(true, "Production", async host =>
        {
            using var client = host.Client();
            using var created = await Create(client); var location = created.Headers.Location!;
            using var first = await client.GetAsync(location); using var hit = await client.GetAsync(location);
            var before = await host.Counts(); Assert.Equal(1, before.GetProperty("getCalls").GetInt32()); Assert.Equal(2, before.GetProperty("cacheGets").GetInt32());
            using var missing = host.Client(false); using var missingResult = await missing.GetAsync(location); await AssertProblem(missingResult, 401, "authentication_required");
            using var invalid = host.Client(); invalid.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-secret");
            using var invalidResult = await invalid.GetAsync(location); await AssertProblem(invalidResult, 401, "authentication_required");
            using var denied = host.Client(permission: "none"); using var deniedResult = await denied.GetAsync(location); await AssertProblem(deniedResult, 403, "authorization_denied");
            var after = await host.Counts(); Assert.Equal(before.GetProperty("cacheGets").GetInt32(), after.GetProperty("cacheGets").GetInt32()); Assert.Equal(before.GetProperty("cacheSets").GetInt32(), after.GetProperty("cacheSets").GetInt32()); Assert.Equal(1, after.GetProperty("getCalls").GetInt32());
            Assert.True(after.GetProperty("authorizations").GetInt32() > before.GetProperty("authorizations").GetInt32());
            foreach (var (tenant, user) in new[] { ("A", "2"), ("B", "1") })
            {
                using var other = host.Client(tenant: tenant, user: user); using var absent = await other.GetAsync(location); await AssertProblem(absent, 404, "not_found");
                using var own = await Create(other, "own", quantity: 7);
                using var streamResponse = await SocketBody.Open(other, "/orders/events");
                using var line = JsonDocument.Parse((await streamResponse.ReadLine())!);
                using var ownDto = JsonDocument.Parse(await own.Content.ReadAsStringAsync());
                Assert.Equal(ownDto.RootElement.GetProperty("id").GetString(), line.RootElement.GetProperty("orderId").GetString());
                streamResponse.Dispose(); await host.Until(x => x.GetProperty("active").GetInt32() == 0);
            }
            Assert.Equal(2, (await host.Counts()).GetProperty("streamDisposes").GetInt32());
        });
    }
    [Fact]
    public async Task Real_Socket_Disconnect_Cleanup_Failure_Is_Observed_And_Joined()
    {
        await SocketHost.RunTest(true, "Production", async host =>
        {
            using var client = host.Client(); using var created = await Create(client);
            await host.Set("cleanup-failure");
            using (var response = await SocketBody.Open(client, "/orders/events"))
            { Assert.NotNull(await response.ReadLine()); }
            await host.Until(x => x.GetProperty("active").GetInt32() == 0 && x.GetProperty("streamDisposes").GetInt32() == 1);
            Assert.Equal(1, (await host.Counts()).GetProperty("streamCancellations").GetInt32());
            Assert.Contains("stream_failure", await File.ReadAllTextAsync(Path.Combine(host.EvidenceDirectory, "stdout.log")));
        });
    }
    [Theory]
    [InlineData("partial", false)]
    [InlineData("complete", true)]
    public async Task Body_Deadline_Cancels_And_Joins_Real_Stalled_Socket(string shape, bool readRemainder)
    {
        SocketHost? owned = null;
        var elapsed = new Stopwatch();
        var error = await Record.ExceptionAsync(() => SocketHost.RunTest(true, "Production", async host =>
        {
            owned = host;
            using var client = host.Client();
            using var body = await SocketBody.Open(client, "/probe/stall/" + shape, TimeSpan.FromMilliseconds(300));
            elapsed.Start();
            using var safetyCancellation = new CancellationTokenSource();
            async Task Safety()
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), safetyCancellation.Token); body.Dispose(); }
                catch (OperationCanceledException) when (safetyCancellation.IsCancellationRequested) { }
            }
            var safety = Safety();
            try
            {
                if (readRemainder) { Assert.Equal("{\"ready\":true}", await body.ReadLine()); await body.ReadToEnd(); }
                else await body.ReadLine();
            }
            finally { elapsed.Stop(); safetyCancellation.Cancel(); await safety; }
        }));
        Assert.IsType<TimeoutException>(error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Owned body cancellation must finish before the outer safety cleanup.");
        Assert.NotNull(owned);
        Assert.True(owned.ExitObserved); Assert.True(owned.DrainsObserved);
        using var joined = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(owned.EvidenceDirectory, "stall-lifecycle.json")));
        Assert.Equal(1, joined.RootElement.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, joined.RootElement.GetProperty("joined").GetInt32());
        Assert.Equal(0, joined.RootElement.GetProperty("active").GetInt32());
    }
    [Fact]
    public async Task Assertion_Failure_Preserves_Identity_And_Joins_Owned_Stalled_Host()
    {
        SocketHost? owned = null; var primary = new IOException("selected assertion failure");
        var error = await Record.ExceptionAsync(() => SocketHost.RunTest(true, "Production", async host =>
        {
            owned = host; using var client = host.Client();
            using var body = await SocketBody.Open(client, "/probe/stall/complete");
            Assert.Equal("{\"ready\":true}", await body.ReadLine());
            throw primary;
        }));
        Assert.Same(primary, error); Assert.NotNull(owned);
        Assert.True(owned.ExitObserved); Assert.True(owned.DrainsObserved);
        using var joined = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(owned.EvidenceDirectory, "stall-lifecycle.json")));
        Assert.Equal(1, joined.RootElement.GetProperty("joined").GetInt32());
        Assert.Equal(0, joined.RootElement.GetProperty("active").GetInt32());
    }
    private static async Task<HttpResponseMessage> Create(HttpClient client, string key = "submission", int quantity = 2)
    { using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new { product = "book", quantity }) }; request.Headers.Add("Idempotency-Key", key); return await client.SendAsync(request); }
    private static async Task AssertProblem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode); var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("secret", text); Assert.DoesNotContain("Exception", text); using var json = JsonDocument.Parse(text);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString()); Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), json.RootElement.GetProperty("correlationId").GetString());
    }
}
public sealed class SocketHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout, _stderr, _exit;
    private readonly string _directory;
    private readonly byte[] _key;
    private readonly string _url;
    private readonly bool _probe;
    private SocketHost(Process process, Task stdout, Task stderr, Task exit, string directory, byte[] key, string url, bool probe)
    { _process = process; _stdout = stdout; _stderr = stderr; _exit = exit; _directory = directory; _key = key; _url = url; _probe = probe; }
    public static async Task<SocketHost> Start(bool probe, string environment)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "eng"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository evidence root is required.");
        var directory = Path.Combine(root.FullName, "artifacts/C6/socket", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(root.FullName, "artifacts/toolchains/dotnet/dotnet");
        if (!File.Exists(dotnet)) dotnet = "dotnet";
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root.FullName };
        start.ArgumentList.Add(probe ? typeof(SocketLauncher).Assembly.Location : Path.Combine(root.FullName, "samples/Meridian.AspNetCore/bin/Release/net10.0/Meridian.AspNetCore.dll"));
        start.ArgumentList.Add("--urls"); start.ArgumentList.Add("http://127.0.0.1:0");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = environment; start.Environment["LocalAuth__Key"] = Convert.ToBase64String(key);
        start.Environment["Orders__DatabasePath"] = Path.Combine(directory, "orders.db"); start.Environment["Orders__Namespace"] = Path.GetFileName(directory);
        start.Environment["Orders__Redis"] = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS") ?? throw new InvalidOperationException("Dedicated Redis required.");
        var process = Process.Start(start)!; var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Drain(StreamReader reader, string file)
        {
            await using var log = new StreamWriter(Path.Combine(directory, file)); string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            { await log.WriteLineAsync(line); await log.FlushAsync(); var index = line.IndexOf("Now listening on: ", StringComparison.Ordinal); if (index >= 0) ready.TrySetResult(line[(index + 18)..].Trim()); if (line.StartsWith("C6_URL=", StringComparison.Ordinal)) ready.TrySetResult(line[7..]); }
        }
        var stdout = Drain(process.StandardOutput, "stdout.log"); var stderr = Drain(process.StandardError, "stderr.log");
        var exited = process.WaitForExitAsync();
        try
        {
            if (await Task.WhenAny(ready.Task, exited).WaitAsync(TimeSpan.FromSeconds(15)) == exited) throw new InvalidOperationException("Socket host exited before readiness; see retained stdout/stderr.");
            var url = await ready.Task;
            var source = probe ? typeof(SocketLauncher).Assembly.Location : Path.Combine(root.FullName, "samples/Meridian.AspNetCore/bin/Release/net10.0/Meridian.AspNetCore.dll");
            await File.WriteAllTextAsync(Path.Combine(directory, "host.json"), JsonSerializer.Serialize(new { pid = process.Id, url, environment, probe, assembly = source, sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))), database = Path.Combine(directory, "orders.db") }));
            return new(process, stdout, stderr, exited, directory, key, url, probe);
        }
        catch { if (!process.HasExited) process.Kill(true); await exited; await Task.WhenAll(stdout, stderr); process.Dispose(); throw; }
    }
    public static async Task RunTest(bool probe, string environment, Func<SocketHost, Task> test)
    {
        var host = await Start(probe, environment); var failures = new List<Exception>();
        try { await test(host); } catch (Exception error) { failures.Add(error); }
        try { await host.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Socket test and owned host cleanup failed.", failures);
    }
    public bool ExitObserved { get; private set; }
    public bool DrainsObserved { get; private set; }
    public string EvidenceDirectory => _directory;
    public HttpClient Client(bool authenticated = true, string tenant = "A", string user = "1", string permission = "orders")
    { var client = new HttpClient { BaseAddress = new Uri(_url), Timeout = TimeSpan.FromSeconds(10) }; if (authenticated) client.DefaultRequestHeaders.Authorization = new("Bearer", LocalToken.Create(_key, tenant, user, permission)); return client; }
    public async Task Set(string scenario) { using var client = Client(); using var response = await client.PostAsync("/probe/state/" + scenario, null); response.EnsureSuccessStatusCode(); }
    public async Task<JsonElement> Counts() { using var client = Client(); return (await client.GetFromJsonAsync<JsonElement>("/probe/counts")).Clone(); }
    public async Task Until(Func<JsonElement, bool> predicate)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!predicate(await Counts())) await Task.Delay(20, deadline.Token); }
    public OrdersDbContext Database() => new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "orders.db"), Pooling = false }.ToString()).Options);
    private bool _disposed;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        try
        {
            if (!_process.HasExited)
            {
                if (_probe)
                {
                    using var client = Client();
                    try { using var response = await client.PostAsync("/probe/stop", null); }
                    catch (HttpRequestException) { /* The stop response can race normal host shutdown. */ }
                }
                else if (OperatingSystem.IsWindows()) _process.Kill(entireProcessTree: true);
                else
                {
                    using var stop = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-TERM", _process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!;
                    await stop.WaitForExitAsync();
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _exit.WaitAsync(deadline.Token); }
                catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
                { failures.Add(new TimeoutException("Owned socket host shutdown deadline expired.", error)); }
            }
        }
        catch (Exception error) { failures.Add(error); }
        // Join the exact owned process and both drains even after stop/assertion/timeout failures.
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); await _exit; ExitObserved = true; }
        catch (Exception error) { failures.Add(error); }
        try { await Task.WhenAll(_stdout, _stderr); DrainsObserved = true; }
        catch (Exception error) { failures.Add(error); }
        _process.Dispose();
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Owned socket host cleanup failed.", failures);
    }
}
