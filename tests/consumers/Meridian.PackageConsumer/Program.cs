using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var configuration = new MapperConfiguration(c => c.CreateMap<Source, Destination>());
var destination = configuration.CreateMapper().Map<Destination>(new Source { Value = 42 });
var services = new ServiceCollection();
services.AddMeridianMediator(c => c.RegisterServicesFromAssemblyContaining<Ping>());
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var reply = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Ping());
if (destination.Value != 42 || reply != 42) throw new InvalidOperationException("Fresh package mapping/mediator consumption failed");
Console.WriteLine("COMMON package mapping=42 mediator=42 isolatedNuGet=true");
public sealed class Source { public int Value { get; set; } }
public sealed class Destination { public int Value { get; set; } }
public sealed record Ping : IRequest<int>;
public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(42);
}
