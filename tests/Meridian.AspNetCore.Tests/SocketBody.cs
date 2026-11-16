namespace Meridian.AspNetCore.Tests;

internal sealed class SocketBody : IDisposable
{
    private readonly CancellationTokenSource _deadline;
    private readonly StreamReader _reader;
    private bool _disposed;
    private SocketBody(HttpResponseMessage response, StreamReader reader, CancellationTokenSource deadline)
    { Response = response; _reader = reader; _deadline = deadline; }
    public HttpResponseMessage Response { get; }
    public static async Task<SocketBody> Open(HttpClient client, string path, TimeSpan? budget = null)
    {
        var deadline = new CancellationTokenSource(budget ?? TimeSpan.FromSeconds(10));
        HttpResponseMessage? response = null;
        try
        {
            response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            return new(response, new StreamReader(stream), deadline);
        }
        catch { deadline.Cancel(); response?.Dispose(); deadline.Dispose(); throw; }
    }
    public async Task<string?> ReadLine()
    {
        try { return await _reader.ReadLineAsync(_deadline.Token); }
        catch (OperationCanceledException error) when (_deadline.IsCancellationRequested)
        { throw new TimeoutException("HTTP smoke body deadline expired.", error); }
    }
    public async Task<string> ReadToEnd()
    {
        try { return await _reader.ReadToEndAsync(_deadline.Token); }
        catch (OperationCanceledException error) when (_deadline.IsCancellationRequested)
        { throw new TimeoutException("HTTP smoke body deadline expired.", error); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _deadline.Cancel(); _reader.Dispose(); Response.Dispose(); _deadline.Dispose();
    }
}
