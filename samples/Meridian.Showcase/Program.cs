namespace Meridian.Showcase;

public static class Program
{
    public static async Task Main()
    {
        Console.WriteLine("Meridian Showcase");
        Console.WriteLine();

        await MappingDemo.Run();
        await QueryMediatorDemo.RunAsync();
        await CommandMediatorDemo.RunAsync();
        await NotificationMediatorDemo.RunAsync();
        await StreamingMediatorDemo.RunAsync();
    }
}
