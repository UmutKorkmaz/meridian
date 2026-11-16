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

        // Count actual handler calls: cache hits must bypass this wrapper.
        var boardExecutions = 0;
        services.AddTransient<IRequestHandler<Backlog.GetTodoBoardQuery, TodoBoardDto>>(sp =>
            new CountingHandler<Backlog.GetTodoBoardQuery, TodoBoardDto>(
                ActivatorUtilities.CreateInstance<Backlog.GetTodoBoardQueryHandler>(sp), () => boardExecutions++));

        using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var board = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Console.WriteLine($"Initial board size: {board.Items.Count}");

        var boardHit = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Require(boardExecutions == 1 && ReferenceEquals(board, boardHit) && board.Count == 0, "board miss then hit");

        var first = await mediator.Send(new Backlog.CreateTodoCommand("Fix build pipeline", "Alice", "High"));
        var afterFirst = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Require(first.Id != Guid.Empty && afterFirst.Count == 1 && afterFirst.Items.Single().Id == first.Id && boardExecutions == 2,
            "create invalidates board and exposes new todo");

        var second = await mediator.Send(new Backlog.CreateTodoCommand("Update release notes", "Bob", "Low"));
        Console.WriteLine($"Created: {first.Title} / {second.Title}");

        var afterSecond = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Require(afterSecond.Count == 2 && boardExecutions == 3, "second create refreshes board");

        var updated = await mediator.Send(new Backlog.StartTodoCommand(first.Id, "Carol"));
        Console.WriteLine($"Started: {updated.Title} by {updated.AssignedTo}");

        var reopened = await mediator.Send(new Backlog.GetTodoBoardQuery());
        Console.WriteLine($"Board after writes: {reopened.Items.Count}");

        var startedItem = reopened.Items.Single(item => item.Id == first.Id);
        Require(updated.State == "InProgress" && startedItem.State == "InProgress" && startedItem.AssignedTo == "Carol"
            && reopened.Count == 2 && boardExecutions == 4, "start invalidates board and exposes changed state");
        await mediator.Send(new Backlog.GetTodoBoardQuery());
        Require(boardExecutions == 4, "refreshed board is cached");
        Console.WriteLine($"PASS cache assertions: board handlers={boardExecutions}; size=0->1->2; state=Open->InProgress");

        var stream = mediator.CreateStream(new Activity.GetTodoActivityStream(first.Id));
        await foreach (var activity in stream)
        {
            Console.WriteLine($"Activity: {activity.Text}");
        }
    }

    private static void Require(bool condition, string assertion)
    {
        if (!condition) throw new InvalidOperationException($"Sample assertion failed: {assertion}");
    }

    private sealed class CountingHandler<TRequest, TResponse>(
        IRequestHandler<TRequest, TResponse> inner, Action onExecution)
        : IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse>
    {
        public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken)
        {
            onExecution();
            return inner.Handle(request, cancellationToken);
        }
    }
}
