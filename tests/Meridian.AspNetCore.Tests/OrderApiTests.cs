using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Meridian.AspNetCore.Tests;

[Trait("Provider", "Redis")]
public sealed class OrderApiTests
{
    private static async Task<HttpResponseMessage> Create(HttpClient client, string key = "submission", string product = "book", int quantity = 2)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders") { Content = JsonContent.Create(new { product, quantity }) };
        request.Headers.Add("Idempotency-Key", key); return await client.SendAsync(request);
    }
    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode); var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body); Assert.DoesNotContain("Exception", body); Assert.DoesNotContain("StackTrace", body);
        var json = JsonDocument.Parse(body).RootElement.Clone(); Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-ID").Single(), json.GetProperty("correlationId").GetString());
        return json;
    }
    [Fact]
    public async Task Valid_Order_Creates_And_Reads()
    {
        await using var app = new ApiFixture(); using var client = app.Client(app.Token()); using var created = await Create(client);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); var dto = await created.Content.ReadFromJsonAsync<OrderDto>();
        using var read = await client.GetAsync(created.Headers.Location); Assert.Equal(dto, await read.Content.ReadFromJsonAsync<OrderDto>());
        using var list = await client.GetAsync("/orders"); Assert.Single((await list.Content.ReadFromJsonAsync<OrderDto[]>())!);
        Assert.Equal((1, 1), await app.Rows());
        var body = await created.Content.ReadAsStringAsync(); Assert.DoesNotContain("scope", body); Assert.DoesNotContain("tenant", body);
        Assert.Equal(1, app.Signals.HandlerCalls); Assert.Equal(1, app.Signals.Completions);
        Assert.All(app.Signals.Correlations.Take(3), x => Assert.Equal(created.Headers.GetValues("X-Correlation-ID").Single(), x));
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Invalid_Request_Returns_400(string environment)
    {
        await using var app = new ApiFixture(environment); using var client = app.Client(app.Token());
        using var validation = await Create(client, quantity: 0); await Problem(validation, HttpStatusCode.BadRequest, "validation_failed");
        using var asyncRule = await Create(client, product: "unavailable"); await Problem(asyncRule, HttpStatusCode.BadRequest, "validation_failed");
        using var malformed = await client.PostAsync("/orders", new StringContent("{secret-malformed", System.Text.Encoding.UTF8, "application/json"));
        await Problem(malformed, HttpStatusCode.BadRequest, "invalid_request"); Assert.Equal(0, app.Signals.Claims); Assert.Equal(0, app.Signals.HandlerCalls);
    }
    [Fact]
    public async Task Missing_Identity_Returns_401()
    {
        await using var app = new ApiFixture(); using var client = app.Client(); using var response = await client.GetAsync("/orders");
        await Problem(response, HttpStatusCode.Unauthorized, "authentication_required"); Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(0, app.Signals.CacheGets); Assert.Equal(0, app.Signals.Claims);
    }
    [Theory]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("wrong-signature")]
    public async Task Invalid_Identity_Challenges(string shape)
    {
        await using var app = new ApiFixture(); var token = shape == "expired" ? LocalToken.Create(app.Key, "A", "1", expires: 1)
            : shape == "wrong-signature" ? LocalToken.Create(new byte[32], "A", "1") : "invalid";
        using var client = app.Client(token); using var response = await client.GetAsync("/orders"); await Problem(response, HttpStatusCode.Unauthorized, "authentication_required");
        Assert.Equal(0, app.Signals.CacheGets);
    }
    [Theory]
    [InlineData("denied")]
    [InlineData("missing-tenant")]
    [InlineData("missing-user")]
    public async Task Denied_Authorization_Returns_403(string shape)
    {
        await using var app = new ApiFixture(); using var client = app.Client(app.Token(shape == "missing-tenant" ? null : "A", shape == "missing-user" ? null : "1", shape == "denied" ? "none" : "orders"));
        using var response = await Create(client); await Problem(response, HttpStatusCode.Forbidden, "authorization_denied"); Assert.Equal(0, app.Signals.Claims); Assert.Equal(0, app.Signals.HandlerCalls);
    }
    [Fact]
    public async Task Completed_Replay_Returns_Original_Submission_And_Does_Not_Write_Again()
    {
        await using var app = new ApiFixture(); using var client = app.Client(app.Token()); using var first = await Create(client);
        using var replay = await Create(client, product: "different-valid-body"); Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        Assert.Equal((1, 1), await app.Rows()); Assert.Equal(1, app.Signals.HandlerCalls); Assert.Equal(2, app.Signals.Authorizations);
        using var invalidReplay = await Create(client, quantity: 0); await Problem(invalidReplay, HttpStatusCode.BadRequest, "validation_failed"); Assert.Equal(2, app.Signals.Claims);
    }
    [Fact]
    public async Task Duplicate_InFlight_Returns_409()
    {
        await using var app = new ApiFixture(); app.Signals.PauseCreate = true; using var client = app.Client(app.Token());
        var operation = Create(client);
        try
        {
            await app.Signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); using var duplicate = await Create(client);
            await Problem(duplicate, HttpStatusCode.Conflict, "idempotency_in_progress"); Assert.Equal(1, app.Signals.HandlerCalls);
        }
        finally { app.Signals.Release.TrySetResult(); using var joined = await operation; Assert.Equal(HttpStatusCode.Created, joined.StatusCode); }
        Assert.Equal((1, 1), await app.Rows());
    }
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Unexpected_Error_Is_Redacted_500(string environment)
    {
        await using var app = new ApiFixture(environment); app.Signals.ThrowAfterSave = true; using var client = app.Client(app.Token());
        using var response = await Create(client); await Problem(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal((0, 0), await app.Rows()); Assert.Equal(0, app.Signals.Completions);
    }
    [Fact]
    public async Task Post_Commit_Uncertainty_Retains_Committed_Row_Without_Replay()
    {
        await using var app = new ApiFixture(); app.Signals.FailCompletion = true; using var client = app.Client(app.Token());
        using var response = await Create(client); await Problem(response, HttpStatusCode.Conflict, "idempotency_outcome_unknown");
        Assert.Equal((1, 1), await app.Rows()); using var second = await Create(client); await Problem(second, HttpStatusCode.Conflict, "idempotency_in_progress"); Assert.Equal(1, app.Signals.HandlerCalls);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unclassified_Or_Aggregate_Completion_Failure_Is_Redacted_500_With_Committed_Work(int kind)
    {
        await using var app = new ApiFixture(); app.Signals.CompletionFailureKind = kind; using var client = app.Client(app.Token());
        using var response = await Create(client); await Problem(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal((1, 1), await app.Rows()); using var replay = await Create(client); await Problem(replay, HttpStatusCode.Conflict, "idempotency_in_progress"); Assert.Equal(1, app.Signals.HandlerCalls);
    }
    [Fact]
    public async Task Retained_Expired_Claim_Returns_Unknown_Without_Handler_Entry()
    {
        await using var app = new ApiFixture(customize: services => services.Replace(ServiceDescriptor.Singleton(new Meridian.Mediator.Redis.RedisIdempotencyOptions
        { ApplicationKeyPrefix = "meridian-orders", LeaseDuration = TimeSpan.FromMilliseconds(100) })));
        using var client = app.Client(app.Token());
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            accessor.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("iss", LocalAuthSettings.Issuer), new System.Security.Claims.Claim("tenant", "A"), new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "1"), new System.Security.Claims.Claim("permission", "orders") }, "local"))
            };
            try
            {
                var claim = await scope.ServiceProvider.GetRequiredService<Meridian.Mediator.Redis.RedisLeasedIdempotencyStore>()
                    .ClaimAsync(IdempotencyKeyComposer.Compose<CreateOrder>("expired"), typeof(OrderDto), default);
                Assert.Equal(LeasedIdempotencyStatus.Claimed, claim.Status);
                await Task.Delay(150);
            }
            finally { accessor.HttpContext = null; }
        }
        using var response = await Create(client, "expired"); await Problem(response, HttpStatusCode.Conflict, "idempotency_outcome_unknown"); Assert.Equal(0, app.Signals.HandlerCalls); Assert.Equal((0, 0), await app.Rows());
    }
    [Theory]
    [InlineData("text/html")]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    public async Task Development_Unsupported_Accept_Still_Redacts(string accept)
    {
        await using var app = new ApiFixture(); app.Signals.ThrowAfterSave = true; using var client = app.Client(app.Token()); client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        using var response = await Create(client); await Problem(response, HttpStatusCode.InternalServerError, "internal_error"); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
    [Fact]
    public async Task Tenant_Cache_Isolation()
    {
        await using var app = new ApiFixture(); using var a = app.Client(app.Token()); using var made = await Create(a); var url = made.Headers.Location;
        using var first = await a.GetAsync(url); using var hit = await a.GetAsync(url); Assert.Equal(HttpStatusCode.OK, hit.StatusCode); Assert.Equal(2, app.Signals.CacheGets); Assert.Equal(1, app.Signals.CacheSets); Assert.Equal(1, app.Signals.GetCalls);
        using var otherUser = app.Client(app.Token(user: "2")); using var otherTenant = app.Client(app.Token(tenant: "B"));
        using var userMiss = await otherUser.GetAsync(url); using var tenantMiss = await otherTenant.GetAsync(url); Assert.Equal(HttpStatusCode.NotFound, userMiss.StatusCode); Assert.Equal(HttpStatusCode.NotFound, tenantMiss.StatusCode);
        Assert.Equal(1, app.Signals.CacheSets); // Negative results do not populate the cache.
        var reads = app.Signals.CacheGets; var authorizations = app.Signals.Authorizations;
        using var denied = app.Client(app.Token(permission: "none")); using var reject = await denied.GetAsync(url); await Problem(reject, HttpStatusCode.Forbidden, "authorization_denied");
        using var missing = app.Client(); using var unauth = await missing.GetAsync(url); Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.Equal(reads, app.Signals.CacheGets); Assert.Equal(authorizations + 1, app.Signals.Authorizations);
    }
    [Fact]
    public async Task Missing_Handler_Is_Redacted_500_Before_Cache()
    {
        await using var app = new ApiFixture(customize: services => services.RemoveAll(typeof(IAuthorizationHandler<>))); using var client = app.Client(app.Token());
        using var response = await client.GetAsync("/orders?orderId=" + Guid.NewGuid()); await Problem(response, HttpStatusCode.InternalServerError, "internal_error"); Assert.Equal(0, app.Signals.CacheGets);
    }
    [Fact]
    public async Task Notification_Failure_Rolls_Back_And_Does_Not_Complete_Claim()
    {
        await using var app = new ApiFixture(); app.Signals.ThrowNotification = true; using var client = app.Client(app.Token());
        using var response = await Create(client); await Problem(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal((0, 0), await app.Rows()); Assert.Equal(1, app.Signals.Notifications); Assert.Equal(0, app.Signals.Completions);
    }
    [Fact]
    public async Task Stream_Authentication_Prevents_Handler_Start()
    {
        await using var app = new ApiFixture(); using var client = app.Client(); using var response = await client.GetAsync("/orders/events");
        await Problem(response, HttpStatusCode.Unauthorized, "authentication_required"); Assert.Equal(0, app.Signals.StreamStarts);
        using var denied = app.Client(app.Token(permission: "none")); using var forbidden = await denied.GetAsync("/orders/events");
        await Problem(forbidden, HttpStatusCode.Forbidden, "authorization_denied"); Assert.Equal(0, app.Signals.StreamStarts);
    }
    [Fact]
    public async Task Correlation_Is_Returned_Without_Internal_Error_Detail()
    {
        await using var app = new ApiFixture(); using var client = app.Client(); client.DefaultRequestHeaders.TryAddWithoutValidation("X-Correlation-ID", new[] { "secret-untrusted", "bad,id" });
        using var response = await client.GetAsync("/orders"); var json = await Problem(response, HttpStatusCode.Unauthorized, "authentication_required"); Assert.Equal(32, json.GetProperty("correlationId").GetString()!.Length);
    }
    [Fact]
    public async Task OpenApi_Describes_Actual_Statuses_And_Ndjson()
    {
        await using var app = new ApiFixture(); using var client = app.Client(); using var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(text); var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/orders").GetProperty("post").GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(paths.GetProperty("/orders/events").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").TryGetProperty("application/x-ndjson", out _));
        Assert.Contains("X-Correlation-ID", text); Assert.Contains("truncation", text);
    }
    [Fact]
    public async Task OpenApi_Get_Orders_200_Describes_List_And_Single_Wire_Shapes()
    {
        await using var app = new ApiFixture(); using var client = app.Client(app.Token()); using var created = await Create(client);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!.OriginalString; Assert.StartsWith("/orders?orderId=", location);
        using var listResponse = await client.GetAsync("/orders"); using var singleResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode); Assert.Equal(HttpStatusCode.OK, singleResponse.StatusCode);
        using var listWire = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()); using var singleWire = JsonDocument.Parse(await singleResponse.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, listWire.RootElement.ValueKind); Assert.Equal(JsonValueKind.Object, singleWire.RootElement.ValueKind);
        using var openApi = await client.GetAsync("/openapi/v1.json"); openApi.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync());
        var get = doc.RootElement.GetProperty("paths").GetProperty("/orders").GetProperty("get");
        var schema = get.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        var branches = schema.GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(2, branches.Length);
        var arrayBranch = Assert.Single(branches, b => b.TryGetProperty("type", out var t) && t.GetString() == "array");
        var objectBranch = Assert.Single(branches, b => b.TryGetProperty("$ref", out _));
        var orderRef = objectBranch.GetProperty("$ref").GetString()!; Assert.Equal(orderRef, arrayBranch.GetProperty("items").GetProperty("$ref").GetString());
        var component = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(orderRef.Split('/').Last());
        var declared = component.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(declared, singleWire.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.All(listWire.RootElement.EnumerateArray(), item => Assert.Equal(declared, item.EnumerateObject().Select(p => p.Name).Order().ToArray()));
        var orderId = Assert.Single(get.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == "orderId");
        Assert.Equal("query", orderId.GetProperty("in").GetString());
        Assert.Contains("orderId", get.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.True(get.GetProperty("responses").TryGetProperty("404", out _));
    }
    [Fact]
    public async Task Initial_Stream_Error_Is_Redacted_Before_Start()
    {
        await using var app = new ApiFixture(); app.Signals.InitialStreamFailure = true; using var client = app.Client(app.Token()); using var response = await client.GetAsync("/orders/events");
        await Problem(response, HttpStatusCode.InternalServerError, "internal_error"); Assert.Equal(1, app.Signals.StreamDisposes);
    }
    [Fact]
    public async Task Canceled_Stream_Disposes_Handler()
    {
        await using var app = new ApiFixture(); using var client = app.Client(app.Token()); using var made = await Create(client);
        using var cancellation = new CancellationTokenSource();
        using (var stream = await client.GetAsync("/orders/events", HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
        {
            using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync()); var line = await reader.ReadLineAsync(cancellation.Token); Assert.Contains("book", line);
            cancellation.Cancel();
        }
        await app.Signals.StreamDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10)); await app.Signals.JoinOperations(); Assert.Equal(1, app.Signals.StreamDisposes); Assert.Equal(1, app.Signals.StreamCancellations);
    }
}
