namespace Meridian.Hexagonal;

public sealed class ConsoleOrderEventSinkAdapter : IOrderEventSinkPort
{
    public void Publish(string message)
    {
        Console.WriteLine($"[event] {message}");
    }
}
