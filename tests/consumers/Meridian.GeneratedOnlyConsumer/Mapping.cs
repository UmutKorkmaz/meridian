using Meridian.Mapping;

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
[GenerateMapper(typeof(Node), typeof(NodeDto), Mode = GeneratedMappingMode.Structural, PreserveReferences = true, GenerateResolver = true)]
[GenerateMapper(typeof(Batch), typeof(BatchDto), Mode = GeneratedMappingMode.Structural, GenerateResolver = true)]
public static partial class Maps { }
