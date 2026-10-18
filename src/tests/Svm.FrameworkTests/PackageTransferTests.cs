using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Svm.FileStorage;
using Svm.Services.Contracts.Framework;
using Svm.Services.Contracts.Packages;
using Xunit;
namespace Svm.FrameworkTests;
[Trait("Category", "Business")]
public sealed class PackageTransferTests
{
    [Theory][InlineData("get-headers")][InlineData("get-body")][InlineData("put-no-read")][InlineData("put-response")][InlineData("progress")][InlineData("host-stop")]
    public async Task ReplicaTransportBudgetsBoundNoProgressButAllowSlowProgressAndReleaseResources(string fault)
    {
        await using var host = await PackageHostFixture.CreateAsync();
        var bytes = RandomNumberGenerator.GetBytes(fault == "put-no-read" ? 24 * 1024 * 1024 : fault == "progress" ? 16 * 1024 * 1024 : 1024 * 1024);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var peer = new FaultPeer(host.B, host.A, fault, bytes.Length, hash);
        var options = host.A with { Execution = host.A.Execution with { PeerResponseSeconds = fault == "progress" ? 2 : 1, DownloadAuthorizationSeconds = 3 }, Nodes = [host.A.Nodes[0], host.A.Nodes[1] with { InternalBaseUri = $"https://127.0.0.1:{peer.Port}/" }] };
        var services = new ServiceCollection().AddSingleton<IUnitOfWork, OutsideWork>().AddSingleton(TimeProvider.System).AddSvmPackageFiles(options);
        await using var provider = services.BuildServiceProvider(); await using var scope = provider.CreateAsyncScope(); var files = scope.ServiceProvider.GetRequiredService<IPackageFiles>();
        var id = Guid.NewGuid(); var leaseToken = Guid.NewGuid(); var fromPeer = fault is "get-headers" or "get-body" or "host-stop";
        var work = new PackageWorkAuthority(Guid.NewGuid(), id, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Upload", 1, Guid.NewGuid(), true, "Running", "Copying", fromPeer ? "node-b" : "node-a", 1, leaseToken, DateTimeOffset.UtcNow.AddMinutes(1), bytes.Length, hash, "node-a");
        if (!fromPeer) { Directory.CreateDirectory(Path.Combine(options.RootPath, "replicas")); await File.WriteAllBytesAsync(host.Replica(options, id), bytes); }
        using var cancel = new CancellationTokenSource(); var watch = Stopwatch.StartNew();
        var operation = files.PrepareReplicasAsync(new(work, leaseToken, 1), _ => Task.CompletedTask, cancel.Token);
        if (fault == "host-stop") { await peer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); }
        else if (fault == "progress") { var facts = await operation.WaitAsync(TimeSpan.FromSeconds(15)); Assert.All(facts, x => Assert.Equal("Healthy", x.State)); Assert.True(watch.Elapsed > TimeSpan.FromSeconds(2)); }
        else { await Assert.ThrowsAsync<TimeoutException>(() => operation.WaitAsync(TimeSpan.FromSeconds(12))); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10)); }
        if (!fromPeer) { using var exclusive = new FileStream(host.Replica(options, id), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        var mutex = Path.Combine(options.RootPath, "locks", "package-" + id.ToString("D") + ".lock");
        using var lockAgain = new FileStream(mutex, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
    private sealed class OutsideWork : IUnitOfWork
    { public Guid? CurrentOperationId => null; public Task<T> ExecuteAsync<T>(Guid id, Func<CancellationToken, Task<T>> action, CancellationToken token) => throw new NotSupportedException(); }
    // Real mTLS fault endpoint, exclusively owned by this test. No production fault switches.
    private sealed class FaultPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _server, _peer; private readonly ConcurrentBag<Task> _connections = []; private readonly Task _accept;
        private readonly string _fault, _hash; private readonly int _size;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal FaultPeer(PackageFileOptions server, PackageFileOptions caller, string fault, int size, string hash)
        { _server = PackageFileOptions.LoadCertificate(server.ServerCertificatePath, server.ServerCertificatePassword); _peer = PackageFileOptions.LoadCertificate(caller.ClientCertificatePath, caller.ClientCertificatePassword); _fault=fault; _size=size; _hash=hash; _listener.Start(); _accept=Accept(); }
        private async Task Accept()
        { try { while (!_stop.IsCancellationRequested) { var c=await _listener.AcceptTcpClientAsync(_stop.Token); _connections.Add(Handle(c)); } } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { } }
        private async Task Handle(TcpClient client)
        {
            using (client) try
            {
                if (_fault == "progress") client.ReceiveBufferSize = 64 * 1024;
                await using var stream = new SslStream(client.GetStream(), false, (_,cert,_,_) => cert?.GetCertHashString() == _peer.GetCertHashString());
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate=_server, ClientCertificateRequired=true, EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13 },_stop.Token);
                var header=new List<byte>(); var one=new byte[1];
                while(header.Count<16384) {if(await stream.ReadAsync(one,_stop.Token)==0) return; header.Add(one[0]); if(header.Count>=4 && header.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10})) break;}
                var text=Encoding.ASCII.GetString(header.ToArray()); Entered.TrySetResult();
                if (text.StartsWith("HEAD",StringComparison.Ordinal)) { await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {_size}\r\nX-Svm-Sha256: {_hash}\r\nConnection: close\r\n\r\n"),_stop.Token); return; }
                if (_fault is "get-headers" or "host-stop") {await Task.Delay(Timeout.Infinite,_stop.Token); return;}
                if (_fault == "get-body")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {_size}\r\nConnection: close\r\n\r\n"),_stop.Token);
                    await stream.WriteAsync(new byte[32768],_stop.Token); await Task.Delay(Timeout.Infinite,_stop.Token); return;
                }
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(),_stop.Token);
                if (_fault=="put-no-read") {await Task.Delay(Timeout.Infinite,_stop.Token); return;}
                long left=_size; var buffer=new byte[32768];
                while(left>0) {var n=await stream.ReadAsync(buffer.AsMemory(0,(int)Math.Min(left,buffer.Length)),_stop.Token); if(n==0)return; left-=n; if(_fault=="progress")await Task.Delay(5,_stop.Token);}
                if(_fault=="put-response") {await Task.Delay(Timeout.Infinite,_stop.Token); return;}
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),_stop.Token);
            }
            catch(Exception e) when(e is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_connections); _server.Dispose(); _peer.Dispose(); _stop.Dispose(); }
    }
}
