using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Svm.FrameworkTests;

// Test-only AMQP wire fault: drop the first server Basic.Ack publisher-confirm and close that connection.
internal sealed class OutboxConfirmProxy(int targetPort) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _connections = [];
    private Task? _accept;
    private int _drop = 1;
    internal TaskCompletionSource Dropped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal void Start() { _listener.Start(); _accept = AcceptAsync(); }
    internal void Resume() => _resume.TrySetResult();
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var server = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                if (Dropped.Task.IsCompleted) await _resume.Task.WaitAsync(connection.Token);
                await server.ConnectAsync(IPAddress.Loopback, targetPort, connection.Token);
                var incoming = client.GetStream(); var outgoing = server.GetStream();
                var upload = incoming.CopyToAsync(outgoing, connection.Token);
                var download = FramesAsync(outgoing, incoming, connection.Token);
                await Task.WhenAny(upload, download); connection.Cancel(); client.Close(); server.Close();
                try { await Task.WhenAll(upload, download); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
    }
    private async Task FramesAsync(NetworkStream source, NetworkStream destination, CancellationToken token)
    {
        var header = new byte[7];
        while (!token.IsCancellationRequested)
        {
            await source.ReadExactlyAsync(header, token);
            var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(3, 4));
            if (size is < 0 or > 16 * 1024 * 1024) throw new IOException("Invalid test AMQP frame.");
            var payload = new byte[size + 1]; await source.ReadExactlyAsync(payload, token);
            if (header[0] == 1 && size >= 4 && BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2)) == 60 &&
                BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2)) == 80 && Interlocked.CompareExchange(ref _drop, 0, 1) == 1)
            { Dropped.TrySetResult(); return; }
            await destination.WriteAsync(header, token); await destination.WriteAsync(payload, token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); _resume.TrySetResult();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_connections); _stop.Dispose();
    }
}
