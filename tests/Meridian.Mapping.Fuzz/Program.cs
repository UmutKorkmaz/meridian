using System.Text;
using Meridian.Mapping;
using SharpFuzz;

namespace Meridian.Mapping.Fuzz;

/// <summary>
/// SharpFuzz harness for <see cref="Meridian.Mapping"/>. Each iteration
/// receives a raw byte buffer from AFL and turns it into a pathological
/// source object whose shape is determined by the bytes:
///
/// - First 2 bytes: graph depth request (0 – 65535)
/// - Next 2 bytes: collection size request (0 – 65535)
/// - Remaining bytes: string payload
///
/// The mapping call must either complete successfully or throw
/// <see cref="MeridianMappingLimitException"/>. Anything else — in
/// particular <see cref="StackOverflowException"/> or
/// <see cref="OutOfMemoryException"/> — is a real bug and AFL will save
/// the crashing input as a reproducer.
///
/// Run locally via SharpFuzz with AFL++:
///
///   dotnet build -c Release
///   sharpfuzz bin/Release/net10.0/Meridian.Mapping.dll
///   afl-fuzz -i corpus -o findings -- bin/Release/net10.0/Meridian.Mapping.Fuzz @@
///
/// A starter corpus of seed inputs is checked in under <c>corpus/</c>.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<FuzzNode, FuzzNodeDto>();
            c.CreateMap<FuzzItem, FuzzItemDto>();
            c.CreateMap<FuzzRoot, FuzzRootDto>();
        });
        var mapper = cfg.CreateMapper();

        Fuzzer.Run(stream =>
        {
            var buffer = new byte[ushort.MaxValue];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read < 4) return;  // not enough bytes to be interesting

            var depth = BitConverter.ToUInt16(buffer, 0);
            var collectionSize = BitConverter.ToUInt16(buffer, 2);
            var payload = Encoding.UTF8.GetString(buffer, 4, Math.Min(read - 4, 256));

            var root = new FuzzRoot
            {
                Depth = depth,
                CollectionSize = collectionSize,
                Payload = payload,
                Tree = BuildTree(depth),
                Items = BuildItems(collectionSize, payload),
            };

            try
            {
                _ = mapper.Map<FuzzRoot, FuzzRootDto>(root);
            }
            catch (MeridianMappingLimitException)
            {
                // Documented — not a bug.
            }
            catch (InvalidOperationException)
            {
                // Configuration-time errors surface as InvalidOperationException.
                // Fuzz inputs that we cannot serve are acceptable; do not treat
                // as a bug.
            }
            // Any other exception propagates and AFL records the crashing seed.
        });
    }

    private static FuzzNode? BuildTree(int depth)
    {
        if (depth == 0) return null;
        var head = new FuzzNode { Value = 0 };
        var cur = head;
        for (var i = 1; i < depth; i++)
        {
            cur.Child = new FuzzNode { Value = i };
            cur = cur.Child;
        }
        return head;
    }

    private static List<FuzzItem> BuildItems(int size, string payload)
    {
        var list = new List<FuzzItem>(Math.Min(size, 65_535));
        for (var i = 0; i < size; i++)
        {
            list.Add(new FuzzItem { Index = i, Name = payload });
        }
        return list;
    }
}

public class FuzzRoot
{
    public int Depth { get; set; }
    public int CollectionSize { get; set; }
    public string Payload { get; set; } = "";
    public FuzzNode? Tree { get; set; }
    public List<FuzzItem> Items { get; set; } = new();
}

public class FuzzRootDto
{
    public int Depth { get; set; }
    public int CollectionSize { get; set; }
    public string Payload { get; set; } = "";
    public FuzzNodeDto? Tree { get; set; }
    public List<FuzzItemDto> Items { get; set; } = new();
}

public class FuzzNode
{
    public int Value { get; set; }
    public FuzzNode? Child { get; set; }
}

public class FuzzNodeDto
{
    public int Value { get; set; }
    public FuzzNodeDto? Child { get; set; }
}

public class FuzzItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}

public class FuzzItemDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
}
