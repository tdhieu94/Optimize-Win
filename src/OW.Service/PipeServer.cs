using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using OW.Contracts;
using StreamJsonRpc;

namespace OW.Service;

/// <summary>
/// Listens on a local named pipe. Only SYSTEM, administrators and the logged-in user can connect,
/// and each client must pass <see cref="ClientVerifier"/> before it can call anything.
/// </summary>
public sealed class PipeServer(OwServiceImpl service, JunkService junk, ClientVerifier verifier, ILogger<PipeServer> logger) : BackgroundService
{
    private const int MaxClients = 4;
    private readonly SemaphoreSlim _slots = new(MaxClients, MaxClients);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            await _slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(first);
                first = false;
            }
            catch (Exception ex)
            {
                _slots.Release();
                logger.LogError(ex, "Could not create the named pipe; another program may be squatting on it");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _slots.Release();
                throw;
            }

            _ = HandleClientAsync(pipe, stoppingToken);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        try
        {
            if (!verifier.IsTrusted(pipe))
            {
                return;
            }

            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
            rpc.AddLocalRpcTarget<IOwService>(new OwClientSession(service, junk, ClientVerifier.GetClientUserSid(pipe)), null);
            rpc.StartListening();
            using var reg = stoppingToken.Register(rpc.Dispose);
            await rpc.Completion.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pipe client session ended with an error");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            _slots.Release();
        }
    }

    private static NamedPipeServerStream CreatePipe(bool firstInstance)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous;
        if (firstInstance)
        {
            // Fails if some other program already created a pipe with our name (pipe squatting).
            options |= PipeOptions.FirstPipeInstance;
        }

        return NamedPipeServerStreamAcl.Create(
            OwPipe.Name,
            PipeDirection.InOut,
            MaxClients,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }
}
