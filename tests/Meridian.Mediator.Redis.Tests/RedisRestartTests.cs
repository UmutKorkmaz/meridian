using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Meridian.Mediator.Redis.Tests;

[Trait("Provider", "Redis")]
public sealed class RedisRestartTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Hard_Redis_Restart_With_Always_Aof_Retains_Pending_And_Completed_Null()
    {
        const string image = "redis:7.4-alpine@sha256:6ab0b6e7381779332f97b8ca76193e45b0756f38d4c0dcda72dbb3c32061ab99";
        var name = "meridian-c4-aof-" + Guid.NewGuid().ToString("N");
        var data = Path.Combine(Path.GetTempPath(), name); Directory.CreateDirectory(data);
        string? id = null;
        try
        {
            id = (await Docker("run", "-d", "--name", name, "--label", "meridian.task=C4-aof", "-p", "127.0.0.1::6379", "--mount", $"type=bind,source={data},target=/data", image, "redis-server", "--appendonly", "yes", "--appendfsync", "always", "--save", "")).Trim();
            var port = (await Docker("port", id, "6379/tcp")).Trim().Split(':')[1]; var endpoint = "127.0.0.1:" + port + ",allowAdmin=true";
            await using var first = await ConnectionMultiplexer.ConnectAsync(endpoint); var store = Store(first);
            var server = first.GetServer(first.GetEndPoints()[0]);
            var before = RunId(await server.InfoAsync("server"));
            output.WriteLine($"container={id} image={image} run_id={before} config={string.Join(';', (await server.ConfigGetAsync("append*")).Select(x => x.Key + '=' + x.Value))}");
            Assert.Contains(await server.ConfigGetAsync("appendfsync"), x => x.Value == "always");
            var pending = await store.ClaimAsync("pending", typeof(string), default);
            var completed = await store.ClaimAsync("completed", typeof(string), default); await store.CompleteAsync(completed.Lease!, null, default);
            await Docker("kill", "--signal", "KILL", id); await Docker("start", id);
            port = (await Docker("port", id, "6379/tcp")).Trim().Split(':')[1]; endpoint = "127.0.0.1:" + port + ",allowAdmin=true";
            await using var fresh = await ConnectionMultiplexer.ConnectAsync(endpoint); var reopened = Store(fresh);
            var after = RunId(await fresh.GetServer(fresh.GetEndPoints()[0]).InfoAsync("server")); Assert.NotEqual(before, after); output.WriteLine($"restart container={id} run_id={after}");
            await CrashRestartTests.WaitExpired(fresh.GetDatabase(), "pending");
            Assert.Equal(LeasedIdempotencyStatus.OutcomeUnknown, (await reopened.ClaimAsync("pending", typeof(string), default)).Status);
            Assert.Equal(pending.Lease!.OwnerToken, (string?)await fresh.GetDatabase().HashGetAsync("pending", "owner"));
            Assert.Null(await fresh.GetDatabase().KeyTimeToLiveAsync("pending"));
            var replay = await reopened.ClaimAsync("completed", typeof(string), default); Assert.Equal(LeasedIdempotencyStatus.Completed, replay.Status); Assert.Null(replay.CachedResponse);
        }
        finally
        {
            if (id is not null) await Docker("rm", "-f", id);
            Directory.Delete(data, recursive: true);
        }
    }
    private static RedisLeasedIdempotencyStore Store(IConnectionMultiplexer connection)
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var registry = new CacheContractRegistry().RegisterContract("text", 1, (JsonTypeInfo<string>)json.GetTypeInfo(typeof(string)));
        return new(connection, registry, new JsonCacheValueSerializer(registry), new() { LeaseDuration = TimeSpan.FromSeconds(2), CompletedRetention = TimeSpan.FromMinutes(10) });
    }
    private static string RunId(IGrouping<string, KeyValuePair<string, string>>[] info) => info.SelectMany(x => x).Single(x => x.Key == "run_id").Value;
    private async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        var result = await stdout; var errors = await stderr; output.WriteLine($"docker {string.Join(' ', args)} exit={process.ExitCode} {result} {errors}");
        if (process.ExitCode != 0) throw new InvalidOperationException("Required isolated Redis restart gate failed: " + errors);
        return result;
    }
}
