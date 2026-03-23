namespace Meridian.VerticalSlice;

public interface ITodoStore
{
    Task<TodoItem[]> GetAllAsync(CancellationToken cancellationToken);
    Task<TodoItem?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<TodoItem> CreateAsync(string title, string owner, string priority, CancellationToken cancellationToken);
    Task UpdateAsync(TodoItem item, CancellationToken cancellationToken);
}

public sealed class InMemoryTodoStore : ITodoStore
{
    private readonly List<TodoItem> _items = [];
    private readonly object _lock = new();

    public Task<TodoItem[]> GetAllAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.ToArray());
        }
    }

    public Task<TodoItem?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var found = _items.FirstOrDefault(item => item.Id == id);
            return Task.FromResult(found);
        }
    }

    public Task<TodoItem> CreateAsync(string title, string owner, string priority, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var item = new TodoItem
            {
                Id = Guid.NewGuid(),
                Title = title,
                Owner = owner,
                State = "Open",
                Priority = priority,
            };
            item.AddEvent($"Created {title} for {owner}");

            _items.Add(item);
            return Task.FromResult(item);
        }
    }

    public Task UpdateAsync(TodoItem item, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _items.FindIndex(existing => existing.Id == item.Id);
            if (index >= 0)
            {
                _items[index] = item;
            }

            return Task.CompletedTask;
        }
    }
}

public sealed class TodoItem
{
    private readonly List<TodoActivityDto> _events = [];

    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string State { get; set; } = "Open";
    public List<TodoActivityDto> EventLines => new(_events);

    public void Start(string actor, string nextState)
    {
        AssignedTo = actor;
        State = nextState;
        AddEvent($"Started by {actor}");
    }

    public void AddEvent(string line) => _events.Add(new TodoActivityDto(line));
}

public sealed record TodoItemDto
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
}

public sealed record TodoBoardDto(List<TodoItemDto> Items, int Count);
public sealed record TodoActivityDto(string Text);
public sealed class TodoBoard
{
    public Guid Id { get; set; }
    public List<TodoItemDto> Items { get; set; } = [];
}

public sealed class TodoProfile : Profile
{
    public TodoProfile()
    {
        CreateMap<TodoItem, TodoItemDto>();
        CreateMap<TodoBoard, TodoBoardDto>();
    }
}
