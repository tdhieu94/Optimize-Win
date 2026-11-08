using System.IO.Pipes;
using OW.Contracts;
using StreamJsonRpc;

namespace OW.Client;

/// <summary>Connects the UI (running as a normal user) to the OW admin service over a local named pipe.</summary>
public sealed class OwServiceClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;

    private OwServiceClient(NamedPipeClientStream pipe, JsonRpc rpc, IOwService proxy)
    {
        _pipe = pipe;
        _rpc = rpc;
        Service = proxy;
    }

    public IOwService Service { get; }

    /// <summary>Connects to the service. Throws <see cref="TimeoutException"/> if the service is not running.</summary>
    public static async Task<OwServiceClient> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            OwPipe.Name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Không kết nối được dịch vụ OW.");
            }

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
            var proxy = rpc.Attach<IOwService>();
            rpc.StartListening();
            return new OwServiceClient(pipe, rpc, proxy);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }
}
