using Meridian.Mediator;
using Meridian.Mediator.Streaming;

namespace Meridian.VerticalSlice;

public static class Activity
{
    public sealed record GetTodoActivityStream(Guid TodoId) : IStreamRequest<TodoActivityDto>;

    public sealed class GetTodoActivityStreamHandler(ITodoStore store)
        : IStreamRequestHandler<GetTodoActivityStream, TodoActivityDto>
    {
        public async IAsyncEnumerable<TodoActivityDto> Handle(
            GetTodoActivityStream request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var item = await store.GetAsync(request.TodoId, cancellationToken);
            if (item is null)
            {
                yield break;
            }

            foreach (var line in item.EventLines)
            {
                yield return new TodoActivityDto(line.Text);
            }
        }
    }
}
