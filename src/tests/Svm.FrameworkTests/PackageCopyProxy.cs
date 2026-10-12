using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
namespace Svm.FrameworkTests;

// Test-owned TLS peer forwarding. Pauses after actual bytes reach the replica; production has no fault switch.
internal sealed class PackageCopyProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly X509Certificate2 _server, _peer;
    private readonly int _target;
    private readonly Task _accept;
    private int _hold;
    private TaskCompletionSource _partial = new(TaskCreationOptions.RunContinuationsAsynchronously), _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal Task PartialCopy => _partial.Task;
    internal PackageCopyProxy(string serverPath, string peerPath, string password, int target)
    { _server = new(serverPath, password); _peer = new(peerPath, password); _target = target; _listener.Start(); _accept = Accept(); }
    internal void HoldNextCopy() { _partial = new(TaskCreationOptions.RunContinuationsAsynchronously); _resume = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref _hold, 1); }
    internal void Resume() => _resume.TrySetResult();
    private async Task Accept()
    {
        try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _connections.Add(Forward(client)); } }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task Forward(TcpClient client)
    {
        using (client)
        try
        {
            await using var source = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && cert.GetCertHashString() == _peer.GetCertHashString());
            await source.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _server, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
            using var target = new TcpClient(); await target.ConnectAsync(IPAddress.Loopback, _target, _stop.Token);
            await using var remote = new SslStream(target.GetStream(), false, (_, cert, _, _) => cert is not null && cert.GetCertHashString() == _server.GetCertHashString());
            await remote.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", ClientCertificates = new() { _peer }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, _stop.Token);
            var header = await Header(source, _stop.Token); var text = Encoding.ASCII.GetString(header); var line = text.Split("\r\n").FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)); var length = line is null ? 0 : long.Parse(line.Split(':')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            text = text[..^2] + "Connection: close\r\n\r\n"; await remote.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
            var response = remote.CopyToAsync(source, _stop.Token);
            if (text.StartsWith("PUT /internal/v1/package-replicas/", StringComparison.Ordinal) && Interlocked.Exchange(ref _hold, 0) == 1)
            {
                var first = Math.Min(length, 32768); await Copy(source, remote, first, _stop.Token); length -= first; await remote.FlushAsync(_stop.Token); _partial.TrySetResult(); await _resume.Task.WaitAsync(_stop.Token);
            }
            await Copy(source, remote, length, _stop.Token); await response;
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
    }
    private static async Task<byte[]> Header(Stream stream, CancellationToken token)
    { var result = new List<byte>(); var b = new byte[1]; while (result.Count < 16384) { if (await stream.ReadAsync(b, token) == 0) throw new EndOfStreamException(); result.Add(b[0]); if (result.Count >= 4 && result.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) return result.ToArray(); } throw new IOException("Owned TLS proxy header limit"); }
    private static async Task Copy(Stream source, Stream target, long size, CancellationToken token)
    { var buffer = new byte[65536]; while (size > 0) { var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(size, buffer.Length)), token); if (n == 0) throw new EndOfStreamException(); await target.WriteAsync(buffer.AsMemory(0, n), token); size -= n; } }
    public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); Resume(); await _accept; await Task.WhenAll(_connections); _server.Dispose(); _peer.Dispose(); _stop.Dispose(); }
}
