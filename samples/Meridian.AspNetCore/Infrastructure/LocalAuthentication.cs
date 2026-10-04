using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;

namespace Meridian.AspNetCore.Infrastructure;

// Local demonstration only. There is no HTTP token issuer; the application owner holds the signing key.
public sealed class LocalAuthSettings(IConfiguration configuration)
{
    public const string Issuer = "meridian-local";
    public const string Audience = "meridian-orders";
    public byte[] Key { get; } = ReadKey(configuration["LocalAuth:Key"]);
    private static byte[] ReadKey(string? value)
    {
        var bytes = Convert.FromBase64String(value ?? throw new InvalidOperationException("Supply an ephemeral LocalAuth:Key."));
        return bytes.Length >= 32 ? bytes : throw new InvalidOperationException("LocalAuth:Key requires at least 32 random bytes.");
    }
}

public sealed record LocalIdentity(string Issuer, string Audience, string? Tenant, string? Subject, string Permission, long Expires);
public static class LocalToken
{
    public static string Create(byte[] key, string? tenant, string? subject, string permission = "orders", long? expires = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new LocalIdentity(LocalAuthSettings.Issuer, LocalAuthSettings.Audience, tenant, subject, permission,
            expires ?? DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds()));
        return Encode(payload) + "." + Encode(HMACSHA256.HashData(key, payload));
    }
    internal static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
}

public sealed class LocalBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, LocalAuthSettings settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        if (header.Count != 1 || !header.ToString().StartsWith("Bearer ", StringComparison.Ordinal) || header.ToString().Length > 4096)
            return Task.FromResult(AuthenticateResult.Fail("Invalid local identity."));
        try
        {
            var pieces = header.ToString()[7..].Split('.');
            if (pieces.Length != 2) return Task.FromResult(AuthenticateResult.Fail("Invalid local identity."));
            var payload = LocalToken.Decode(pieces[0]); var signature = LocalToken.Decode(pieces[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(settings.Key, payload))) return Task.FromResult(AuthenticateResult.Fail("Invalid local identity."));
            var token = JsonSerializer.Deserialize<LocalIdentity>(payload);
            if (token is null || token.Issuer != LocalAuthSettings.Issuer || token.Audience != LocalAuthSettings.Audience || token.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return Task.FromResult(AuthenticateResult.Fail("Invalid local identity."));
            var claims = new List<Claim> { new("iss", token.Issuer), new("permission", token.Permission) };
            if (token.Tenant is not null) claims.Add(new("tenant", token.Tenant));
            if (token.Subject is not null) claims.Add(new(ClaimTypes.NameIdentifier, token.Subject));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { return Task.FromResult(AuthenticateResult.Fail("Invalid local identity.")); }
    }
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401; Response.Headers.WWWAuthenticate = "Bearer"; return Task.CompletedTask;
    }
}

public sealed class AuthenticatedOrderScope(IHttpContextAccessor accessor, IConfiguration configuration) : ICacheKeyScope
{
    private string? _snapshot;
    private OrderIdentitySnapshot? _identity;
    public OrderIdentitySnapshot Identity { get { GetScope(); return _identity!; } }
    public string GetScope()
    {
        var user = accessor.HttpContext?.User;
        if (user is null || !user.Identities.Any(x => x.IsAuthenticated)) throw new UnauthorizedException("Identity required.");
        string Claim(string name)
        {
            var values = user.FindAll(name).Select(x => x.Value).ToArray();
            if (values.Length > 1) throw new InvalidOperationException("Contradictory authenticated identity.");
            if (values.Length == 0 || string.IsNullOrWhiteSpace(values[0]) || values[0].Length > 128) throw new UnauthorizedException("Complete identity required.");
            return values[0];
        }
        var space = configuration["Orders:Namespace"];
        if (string.IsNullOrWhiteSpace(space)) throw new InvalidOperationException("Select an isolated application namespace.");
        var identity = new OrderIdentitySnapshot(space, Claim("iss"), Claim("tenant"), Claim(ClaimTypes.NameIdentifier), Claim("permission"));
        var next = LocalToken.Encode(JsonSerializer.SerializeToUtf8Bytes(new[] { identity.Namespace, identity.Issuer, identity.Tenant, identity.Subject, identity.Permission }));
        if (_snapshot is not null && _snapshot != next) throw new InvalidOperationException("Authenticated scope changed during a request.");
        _identity ??= identity;
        return _snapshot ??= next;
    }
    public void Authorize()
    {
        GetScope();
        if (accessor.HttpContext!.User.FindFirst("permission")?.Value != "orders") throw new UnauthorizedException("Permission denied.");
    }
}

public sealed record OrderIdentitySnapshot(string Namespace, string Issuer, string Tenant, string Subject, string Permission);
