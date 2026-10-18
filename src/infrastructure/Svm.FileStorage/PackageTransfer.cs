using System.Net;

namespace Svm.FileStorage;

// The phase timer covers connection/response waits; byte progress resets only
// the streaming idle timer. A progressing large file has no overall time limit.
internal sealed class PackageTransfer(int responseSeconds, int idleSeconds, CancellationToken caller) : IDisposable
{
    private readonly CancellationTokenSource _budget = CancellationTokenSource.CreateLinkedTokenSource(caller);
    public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, Stream? content = null, long? length = null)
    {
        _budget.CancelAfter(TimeSpan.FromSeconds(responseSeconds));
        if (content is not null) { request.Content = new ProgressContent(content, length!.Value, _budget, responseSeconds, idleSeconds); request.Content.Headers.ContentType = new("application/octet-stream"); }
        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _budget.Token);
            _budget.CancelAfter(Timeout.InfiniteTimeSpan);
            return response; // Response content has its own streaming idle budget.
        }
        catch (Exception e) when (!caller.IsCancellationRequested && _budget.IsCancellationRequested && e is OperationCanceledException or HttpRequestException or IOException)
        { throw new TimeoutException("Package transfer made no progress.", e); }
    }
    public void Dispose() => _budget.Dispose();

    private sealed class ProgressContent(Stream input, long length, CancellationTokenSource budget, int responseSeconds, int idleSeconds) : HttpContent
    {
        protected override bool TryComputeLength(out long size) { size = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Copy(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => Copy(stream);
        private async Task Copy(Stream output)
        {
            var buffer = new byte[64 * 1024]; long sent = 0;
            budget.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            while (true)
            {
                var count = await input.ReadAsync(buffer, budget.Token);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), budget.Token);
                sent = checked(sent + count);
                budget.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            }
            if (sent != length) throw new IOException("Package length changed during transfer.");
            // Keep this timer alive until SendAsync has received the response headers.
            budget.CancelAfter(TimeSpan.FromSeconds(responseSeconds));
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
