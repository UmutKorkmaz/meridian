using Meridian.Mapping;

namespace Meridian.NativeAot.Fixture;

// Explicit generated DTO pairs. Handlers and checks call the generated Maps methods directly;
// there is no runtime Mapper, expression compilation or handwritten assignment fallback.
public sealed class Order
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class OrderDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Quantity { get; set; }
}

public sealed class Node
{
    public int Value { get; set; }
    public Node? Next { get; set; }
}

public sealed class NodeDto
{
    public long Value { get; set; }
    public NodeDto? Next { get; set; }
}

public sealed record Batch(IEnumerable<Node> Nodes);

public sealed record BatchDto(IReadOnlyList<NodeDto> Nodes);

[GenerateMapper(typeof(Order), typeof(OrderDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
[GenerateMapper(typeof(Node), typeof(NodeDto), Mode = GeneratedMappingMode.Structural, PreserveReferences = true, GenerateResolver = true)]
[GenerateMapper(typeof(Batch), typeof(BatchDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
public static partial class Maps { }
