using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Meridian.Mediator.Redis.Tests;

[Trait("Provider", "Redis")]
public sealed class CrashRestartTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("after-effect", "effect-recorded", 1)]
    [InlineData("before-effect", "claimed-before-effect", 0)]
    public async Task Killed_Application_Cannot_Reexecute_Pending_Work(string mode, string barrier, int effects)
    {
        await using var f = await RedisFixture.Create(); var logical = "meridian:idempotency:v1:12:CrashRequest:crash"; var key = f.Key(logical);
        var marker = Path.Combine(Path.GetTempPath(), "meridian-c4-" + Guid.NewGuid().ToString("N") + ".marker");
        try
        {
            using (var child = Start(f.Prefix, mode, marker, 2500))
            {
                try { await Signal(child, barrier); Assert.True(await f.Second.GetDatabase().KeyExistsAsync(key)); }
                finally { if (!child.HasExited) Kill(child); }
                Assert.Equal(effects, Count(marker));
            }
            var owner = (string)(await f.Second.GetDatabase().HashGetAsync(key, "owner"))!;
            using (var restarted = Start(f.Prefix, "replay", marker, 2500)) { await Signal(restarted, "status:inflight"); await Exit(restarted); }
            await WaitExpired(f.Second.GetDatabase(), key);
            using (var restarted = Start(f.Prefix, "replay", marker, 2500)) { await Signal(restarted, "status:unknown"); await Exit(restarted); }
            Assert.Equal(effects, Count(marker)); Assert.Null(await f.Second.GetDatabase().KeyTimeToLiveAsync(key));
            if (effects == 0)
            {
                await f.Store(true).ReconcileAsync(logical, owner, IdempotencyReconciliation.SafeToRetry, null, default);
                using var safe = Start(f.Prefix, "replay", marker, 2500); await Signal(safe, "result:null"); await Exit(safe); Assert.Equal(1, Count(marker));
            }
        }
        finally { File.Delete(marker); }
    }
    [Fact]
    public async Task Killed_After_Completion_Replays_Null_Without_Another_Effect()
    {
        await using var f = await RedisFixture.Create(); f.Key("meridian:idempotency:v1:12:CrashRequest:crash");
        var marker = Path.Combine(Path.GetTempPath(), "meridian-c4-" + Guid.NewGuid().ToString("N") + ".marker");
        try
        {
            using (var child = Start(f.Prefix, "after-completion", marker, 3000)) { await Signal(child, "completion-recorded"); Kill(child); }
            using var replay = Start(f.Prefix, "replay", marker, 3000); await Signal(replay, "result:null"); await Exit(replay); Assert.Equal(1, Count(marker));
        }
        finally { File.Delete(marker); }
    }
    [Fact]
    public void Crash_Fixture_Is_Bound_To_Actual_Test_Configuration()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        Assert.Equal(configuration, new FileInfo(FixturePath()).Directory!.Parent!.Name);
    }
    [Fact]
    public async Task Exit_Timeout_Kills_And_Joins_Only_The_Recorded_Child()
    {
        using var child = Start("unused", "exit-hang", "unused", 3000);
        try
        {
            await Signal(child, "exit-hang-ready");
            await Assert.ThrowsAsync<TimeoutException>(() => Exit(child, TimeSpan.FromMilliseconds(30)));
            Assert.True(child.HasExited, "Exit timeout left the recorded child alive.");
        }
        finally { if (!child.HasExited) Kill(child); }
    }
    private static string FixturePath()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var tfm = new DirectoryInfo(AppContext.BaseDirectory).Name;
        return Path.Combine(root, "tests", "Meridian.Mediator.Redis.CrashFixture", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, tfm, "Meridian.Mediator.Redis.CrashFixture.dll");
    }
    private Process Start(string prefix, string mode, string marker, int leaseMs)
    {
        var fixture = FixturePath();
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var start = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { fixture, Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS") ?? "unused", prefix, mode, marker, leaseMs.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture)));
        var child = Process.Start(start)!;
        output.WriteLine(JsonSerializer.Serialize(new { child.Id, fixture, sha256 = digest, mode, host }));
        return child;
    }
    private async Task Signal(Process child, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            { output.WriteLine($"pid={child.Id} {line}"); if (line == expected) return; }
            throw new InvalidOperationException("Child exited without expected barrier: " + await child.StandardError.ReadToEndAsync(timeout.Token));
        }
        catch (Exception primary)
        {
            try { if (!child.HasExited) Kill(child); }
            catch (Exception cleanup) { throw new AggregateException("Child signal and owned cleanup failed.", primary, cleanup); }
            throw;
        }
    }
    private void Kill(Process child)
    {
        var pid = child.Id; child.Kill(entireProcessTree: true); Assert.True(child.WaitForExit(10000)); output.WriteLine($"killed recorded pid={pid} exit={child.ExitCode}");
    }
    private async Task Exit(Process child, TimeSpan? timeout = null)
    {
        try { await child.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(10)); Assert.Equal(0, child.ExitCode); }
        catch (Exception primary)
        {
            try { if (!child.HasExited) Kill(child); }
            catch (Exception cleanup) { throw new AggregateException("Child exit and owned cleanup failed.", primary, cleanup); }
            throw;
        }
    }
    private static int Count(string marker) => File.Exists(marker) ? File.ReadAllLines(marker).Length : 0;
    internal static async Task WaitExpired(IDatabase database, string key)
    {
        var deadline = (long)await database.HashGetAsync(key, "leaseUntilMs");
        for (var i = 0; i < 150; i++)
        {
            var time = (RedisResult[])(await database.ExecuteAsync("TIME"))!;
            if ((long)time[0] * 1000 + (long)time[1] / 1000 >= deadline) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Owned pending lease did not expire within watchdog.");
    }
}
