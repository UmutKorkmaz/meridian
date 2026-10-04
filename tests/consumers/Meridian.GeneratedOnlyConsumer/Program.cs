using Meridian.Mapping;

var source = new Node { Value = 42 };
source.Next = source;
var mapped = Maps.MapToNodeDto(source);
Check(mapped.Value == 42 && ReferenceEquals(mapped, mapped.Next), "cycle");
var batch = Maps.MapToBatchDto(new Batch(new[] { source, source }));
Check(batch.Nodes.Count == 2 && ReferenceEquals(batch.Nodes[0], batch.Nodes[1]), "collection identity");
Check(Maps.MapToNodeDto(source, new GeneratedMappingOptions { MaxDepth = 1 }).Next is null, "depth");
try
{
    Maps.MapToBatchDto(new Batch(new[] { source, source }), new GeneratedMappingOptions { MaxCollectionItems = 1 });
    throw new InvalidOperationException("collection limit was ignored");
}
catch (MeridianMappingLimitException) { }
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();
try
{
    Maps.MapToNodeDto(source, new GeneratedMappingOptions { CancellationToken = cancellation.Token });
    throw new InvalidOperationException("cancellation was ignored");
}
catch (OperationCanceledException) { }
Console.WriteLine("generated-only: value=42 cycle=true collectionIdentity=true depth=true limit=true cancellation=true");
static void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException(name);
}
