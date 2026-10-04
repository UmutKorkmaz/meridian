using Microsoft.AspNetCore.Http;
using Meridian.AspNetCore.Infrastructure;
using Meridian.Mediator.Behaviors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Meridian.AspNetCore.Tests;
// Compiled into the test assembly only. The ordinary sample has no fault routes or environment switches.
public static class SocketLauncher
{
    public static async Task Main(string[] args)
    {
        var state = new Signals();
        var app = await OrderApplication.BuildAsync(args, builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            ApiFixture.ConfigureObservers(builder.Services, state);
        });
        app.MapPost("/probe/state/{scenario}", (string scenario) =>
        {
            switch (scenario)
            {
                case "pause": state.PauseCreate = true; break;
                case "release": state.Release.TrySetResult(); break;
                case "initial": state.InitialStreamFailure = true; break;
                case "late": state.LateStreamFailure = true; break;
                case "unknown": state.FailCompletion = true; break;
                case "unexpected": state.ThrowAfterSave = true; break;
                case "cleanup": state.GateCleanup = true; break;
                case "cleanup-release": state.CleanupRelease.TrySetResult(); break;
                case "cleanup-failure": state.CleanupFailure = true; break;
                case "reset": state.InitialStreamFailure = state.LateStreamFailure = state.FailCompletion = state.ThrowAfterSave = state.GateCleanup = state.CleanupFailure = false; break;
                default: return Results.BadRequest();
            }
            return Results.NoContent();
        });
        app.MapGet("/probe/counts", () => new
        {
            state.HandlerCalls,
            state.CacheGets,
            state.CacheSets,
            state.Authorizations,
            state.GetCalls,
            state.Claims,
            state.StreamStarts,
            state.StreamDisposes,
            state.StreamCancellations,
            active = state.Active,
            cleanupEntered = state.CleanupEntered.Task.IsCompleted,
            entered = state.Entered.Task.IsCompleted
        });
        app.MapGet("/probe/fault/{shape}", (string shape) =>
        {
            Exception error = shape switch
            {
                "missing" => new MissingAuthorizationHandlerException(typeof(SocketLauncher)),
                "unknown" => new IdempotencyOutcomeUnknownException(),
                "aggregate" => new AggregateException(new ValidationException([]), new IOException("secret-cleanup")),
                "denied" => new UnauthorizedException("secret-denied"),
                "validation" => new ValidationException([new("secret", "secret-value")]),
                _ => new IOException("secret-unexpected")
            };
            throw error;
#pragma warning disable CS0162
            return Results.NoContent();
#pragma warning restore CS0162
        });
        app.MapPost("/probe/stop", (IHostApplicationLifetime lifetime) =>
        {
            state.Release.TrySetResult(); state.CleanupRelease.TrySetResult(); lifetime.StopApplication(); return Results.NoContent();
        });
        await app.StartAsync();
        Console.WriteLine("C6_URL=" + app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        await app.WaitForShutdownAsync();
        await app.DisposeAsync();
    }
}
