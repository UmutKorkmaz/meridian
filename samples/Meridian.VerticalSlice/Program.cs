using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.VerticalSlice;

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();
        services.AddVerticalSliceServices();

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var board = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Console.WriteLine($"Initial board size: {board.Items.Count}");

        var first = await mediator.Send(new Backlog.CreateTodoCommand("Fix build pipeline", "Alice", "High"));
        var second = await mediator.Send(new Backlog.CreateTodoCommand("Update release notes", "Bob", "Low"));
        Console.WriteLine($"Created: {first.Title} / {second.Title}");

        var updated = await mediator.Send(new Backlog.StartTodoCommand(first.Id, "Carol"));
        Console.WriteLine($"Started: {updated.Title} by {updated.AssignedTo}");

        var reopened = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Console.WriteLine($"Board after writes: {reopened.Items.Count}");

        var stream = mediator.CreateStream(new Activity.GetTodoActivityStream(first.Id));
        await foreach (var activity in stream)
        {
            Console.WriteLine($"Activity: {activity.Text}");
        }
    }
}
