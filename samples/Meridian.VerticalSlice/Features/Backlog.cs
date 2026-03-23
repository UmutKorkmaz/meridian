using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;

namespace Meridian.VerticalSlice;

public static class Backlog
{
    public sealed record GetTodoBoardQuery : IRequest<TodoBoardDto>, ICacheableQuery
    {
        public string CacheKey => "vs:todo:board";
        public TimeSpan? CacheDuration => TimeSpan.FromMinutes(5);
    }

    public sealed class GetTodoBoardQueryHandler(IMapper mapper, ITodoStore store) : IRequestHandler<GetTodoBoardQuery, TodoBoardDto>
    {
        public async Task<TodoBoardDto> Handle(GetTodoBoardQuery request, CancellationToken cancellationToken)
        {
            var items = await store.GetAllAsync(cancellationToken);
            var projected = items.Select(mapper.Map<TodoItemDto>).ToList();
            return new TodoBoardDto(projected, projected.Count);
        }
    }

    public sealed class GetTodoBoardQueryValidator : IValidator<GetTodoBoardQuery>
    {
        public Task<ValidationResult> ValidateAsync(GetTodoBoardQuery instance, CancellationToken cancellationToken)
            => Task.FromResult(new ValidationResult());
    }

    public sealed record CreateTodoCommand(string Title, string Owner, string Priority) : IRequest<TodoItemDto>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => ["vs:todo:board"];
    }

    public sealed class CreateTodoCommandHandler(IMapper mapper, ITodoStore store, IPublisher publisher)
        : IRequestHandler<CreateTodoCommand, TodoItemDto>
    {
        public async Task<TodoItemDto> Handle(CreateTodoCommand request, CancellationToken cancellationToken)
        {
            var created = await store.CreateAsync(request.Title, request.Owner, request.Priority, cancellationToken);
            await publisher.Publish(new TodoCreatedNotification(created.Id, request.Owner), cancellationToken);
            return mapper.Map<TodoItemDto>(created);
        }
    }

    public sealed class CreateTodoCommandValidator : IValidator<CreateTodoCommand>
    {
        public Task<ValidationResult> ValidateAsync(CreateTodoCommand instance, CancellationToken cancellationToken)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(instance.Title))
            {
                result.Errors.Add(new ValidationError(nameof(instance.Title), "Title is required."));
            }

            if (string.IsNullOrWhiteSpace(instance.Owner))
            {
                result.Errors.Add(new ValidationError(nameof(instance.Owner), "Owner is required."));
            }

            return Task.FromResult(result);
        }
    }

    public sealed record StartTodoCommand(Guid TodoId, string Actor) : IRequest<TodoItemDto>, ICacheInvalidatingRequest
    {
        public string[] CacheKeysToInvalidate => ["vs:todo:board"];
    }

    public sealed class StartTodoCommandHandler(ITodoStore store, IMapper mapper, IPublisher publisher)
        : IRequestHandler<StartTodoCommand, TodoItemDto>
    {
        public async Task<TodoItemDto> Handle(StartTodoCommand request, CancellationToken cancellationToken)
        {
            var item = await store.GetAsync(request.TodoId, cancellationToken)
                ?? throw new InvalidOperationException("Todo item not found.");

            item.Start(request.Actor, "InProgress");

            await store.UpdateAsync(item, cancellationToken);
            await publisher.Publish(new TodoStateChangedNotification(item.Id, request.Actor, item.State), cancellationToken);

            return mapper.Map<TodoItemDto>(item);
        }
    }

    public sealed class StartTodoCommandValidator : IValidator<StartTodoCommand>
    {
        public Task<ValidationResult> ValidateAsync(StartTodoCommand instance, CancellationToken cancellationToken)
        {
            var result = new ValidationResult();

            if (instance.TodoId == Guid.Empty)
            {
                result.Errors.Add(new ValidationError(nameof(instance.TodoId), "TodoId is required."));
            }

            if (string.IsNullOrWhiteSpace(instance.Actor))
            {
                result.Errors.Add(new ValidationError(nameof(instance.Actor), "Actor is required."));
            }

            return Task.FromResult(result);
        }
    }

    public sealed record TodoCreatedNotification(Guid TodoId, string Owner) : INotification;
    public sealed class TodoAuditHandler : INotificationHandler<TodoCreatedNotification>
    {
        public Task Handle(TodoCreatedNotification notification, CancellationToken cancellationToken)
        {
            Console.WriteLine($"[audit] TodoCreated id={notification.TodoId} owner={notification.Owner}");
            return Task.CompletedTask;
        }
    }

    public sealed record TodoStateChangedNotification(Guid TodoId, string Actor, string State) : INotification;
    public sealed class TodoStateChangedHandler : INotificationHandler<TodoStateChangedNotification>
    {
        public Task Handle(TodoStateChangedNotification notification, CancellationToken cancellationToken)
        {
            Console.WriteLine($"[audit] TodoStateChanged id={notification.TodoId} actor={notification.Actor} state={notification.State}");
            return Task.CompletedTask;
        }
    }
}
