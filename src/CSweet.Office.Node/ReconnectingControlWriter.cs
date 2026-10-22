using CSweet.Office.Contracts.ControlPlane;
using Grpc.Core;

namespace CSweet.Office.Node;

// Only idempotent, authenticated Office status/lease messages cross this writer.
// Workload tunnels use the process-owned channel, independently of control reconnects.
internal sealed class ReconnectingControlWriter(TimeSpan reconnectGrace) : IClientStreamWriter<OfficeControlMessage>
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private TaskCompletionSource<IClientStreamWriter<OfficeControlMessage>> _ready = NewSignal();
    private IClientStreamWriter<OfficeControlMessage>? _current;

    public WriteOptions? WriteOptions { get; set; }
    public bool IsConnected { get { lock (_sync) return _current is not null; } }

    public void Attach(IClientStreamWriter<OfficeControlMessage> writer)
    {
        lock (_sync)
        {
            _current = writer;
            _ready.TrySetResult(writer);
        }
    }

    public void Detach(IClientStreamWriter<OfficeControlMessage> writer)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, writer)) return;
            _current = null;
            _ready = NewSignal();
        }
    }

    public Task CompleteAsync() => Task.CompletedTask;
    public Task WriteAsync(OfficeControlMessage message) => WriteAsync(message, CancellationToken.None);

    public async Task WriteAsync(OfficeControlMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(reconnectGrace);
        while (true)
        {
            Task<IClientStreamWriter<OfficeControlMessage>> ready;
            lock (_sync) ready = _ready.Task;
            var writer = await ready.WaitAsync(timeout.Token);
            try
            {
                await WriteConnectedAsync(writer, message, timeout.Token);
                return;
            }
            catch (Exception exception) when (IsTransient(exception) && !timeout.IsCancellationRequested)
            {
                Detach(writer);
                // The next control connection revalidates Office session and assignment epochs.
            }
        }
    }

    public async Task WriteConnectedAsync(IClientStreamWriter<OfficeControlMessage> writer,
        OfficeControlMessage message, CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            lock (_sync)
                if (!ReferenceEquals(_current, writer))
                    throw new RpcException(new Status(StatusCode.Unavailable, "The Office control stream is reconnecting."));
            await writer.WriteAsync(message, cancellationToken);
        }
        finally { _writes.Release(); }
    }

    internal static bool IsTransient(Exception exception) => exception is IOException or HttpRequestException ||
        exception is RpcException
        {
            StatusCode: StatusCode.Unavailable or StatusCode.Unknown or
            StatusCode.Internal or StatusCode.DeadlineExceeded or StatusCode.Cancelled
        };

    private static TaskCompletionSource<IClientStreamWriter<OfficeControlMessage>> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
