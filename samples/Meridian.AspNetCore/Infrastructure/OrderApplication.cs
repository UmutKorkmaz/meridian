using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
using Meridian.Mapping.Extensions;
using Meridian.Mediator.AspNetCore;
using Meridian.Mediator.Caching;
using Meridian.Mediator.EntityFrameworkCore;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.FluentValidation;
using Meridian.Mediator.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace Meridian.AspNetCore.Infrastructure;

public static class OrderApplication
{
    public static async Task<WebApplication> BuildAsync(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddMeridianProblemDetails();
        builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);
        builder.Services.AddAuthentication("Local").AddScheme<AuthenticationSchemeOptions, LocalBearerHandler>("Local", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<LocalAuthSettings>();
        builder.Services.AddScoped<AuthenticatedOrderScope>();
        builder.Services.AddMeridianCacheScope<AuthenticatedOrderScope>();
        builder.Services.AddMeridianMemoryCaching(o => o.ApplicationKeyPrefix = "meridian-orders");
        var registry = new CacheContractRegistry().RegisterContract("orders.dto.v1", 1, OrdersJsonContext.Default.OrderDto);
        builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(
            sp.GetRequiredService<IConfiguration>()["Orders:Redis"] ?? throw new InvalidOperationException("Select an isolated Orders:Redis endpoint.")));
        builder.Services.AddMeridianRedisIdempotency(registry, o => o.ApplicationKeyPrefix = "meridian-orders");
        builder.Services.AddDbContext<OrdersDbContext>((sp, options) =>
        {
            var path = sp.GetRequiredService<IConfiguration>()["Orders:DatabasePath"];
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("Select an absolute disposable Orders:DatabasePath.");
            options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        });
        builder.Services.AddMeridianEfCoreTransactions<OrdersDbContext>();
        builder.Services.AddMeridianMapping(typeof(OrderProfile).Assembly);
        builder.Services.AddMeridianMediator(c => c.RegisterServicesFromAssemblyContaining<CreateOrder>()
            .AddCorrelationIdBehavior(-1000).AddAuthorizationBehavior(-900).AddValidationBehavior(-800)
            .AddIdempotencyBehavior(-600).AddCachingBehavior(-500).AddTransactionBehavior(-400));
        builder.Services.AddScoped(typeof(Meridian.Mediator.Behaviors.IAuthorizationHandler<>), typeof(OrderAuthorization<>));
        builder.Services.AddMeridianFluentValidation(typeof(CreateOrderValidator).Assembly);
        builder.Services.AddOpenApi(options => options.AddOperationTransformer((operation, context, token) =>
        {
            foreach (var response in operation.Responses!.Values.OfType<Microsoft.OpenApi.OpenApiResponse>())
            {
                response.Headers ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiHeader>();
                response.Headers["X-Correlation-ID"] = new Microsoft.OpenApi.OpenApiHeader { Description = "Server-generated correlation identifier.", Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String } };
            }
            operation.Description = (operation.Description ?? "") + " Every response carries X-Correlation-ID. POST failures do not prove rollback; never automatically replay business work.";
            return Task.CompletedTask;
        }));
        configure?.Invoke(builder);
        var app = builder.Build();
        await using (var startup = app.Services.CreateAsyncScope())
        {
            _ = startup.ServiceProvider.GetRequiredService<LocalAuthSettings>();
            await startup.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.EnsureCreatedAsync();
            await startup.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase().PingAsync();
        }
        app.UseMiddleware<OrderCorrelationMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages(context => OrderHttp.WriteProblem(context.HttpContext, context.HttpContext.Response.StatusCode));
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOrderEndpoints();
        app.MapOpenApi();
        return app;
    }
}
