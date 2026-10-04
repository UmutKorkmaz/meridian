using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Meridian.Mediator.AspNetCore;
using Meridian.Mediator.Behaviors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
builder.Services.AddMeridianProblemDetails().AddMeridianProblemDetails();
await using var app = builder.Build();
app.Use((context, next) =>
{
    context.TraceIdentifier = "consumer-correlation";
    context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "local-user") }, "local-test"));
    return next(context);
});
app.UseExceptionHandler();
app.MapGet("/{shape}", (Func<string, IResult>)(shape => throw shape switch
{
    "validation" => new ValidationException(new[] { new ValidationError("secret-property", "secret-value") }),
    "denied" => new UnauthorizedException("secret-value"),
    _ => new IOException("secret-value")
}));
await app.StartAsync();
try
{
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    foreach (var row in new[] { ("validation",400,"validation_failed"),("denied",403,"authorization_denied"),("unexpected",500,"internal_error") })
    {
        using var response = await client.GetAsync("/" + row.Item1); var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        if ((int)response.StatusCode != row.Item2 || json.RootElement.GetProperty("code").GetString() != row.Item3 || text.Contains("secret",StringComparison.Ordinal)
            || json.RootElement.GetProperty("correlationId").GetString() != response.Headers.GetValues("X-Correlation-ID").Single()) throw new InvalidOperationException("HTTP consumer contract failed.");
    }
}
finally { await app.StopAsync(); }
foreach (var assembly in new[] { typeof(MeridianExceptionHandler).Assembly, typeof(ValidationException).Assembly })
    Console.WriteLine($"loadedHttp={assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly.Location))).ToLowerInvariant()}");
Console.WriteLine("httpStatus=400,403,500 correlation=true redacted=true registration=idempotent");
