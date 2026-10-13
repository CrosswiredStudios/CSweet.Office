using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using CSweet.Office.Contracts.Guest;
using Google.Protobuf;

namespace CSweet.Office.Node;

/// <summary>One assignment's bounded, unprivileged socket relay. It exposes no host listener.</summary>
internal sealed class RuntimeInternetEgress(PublicInternetPolicy policy,
    Func<EgressFrame, CancellationToken, Task> send, CancellationToken lifetime,
    Func<System.Net.IPAddress, int, bool, CancellationToken, Task<Socket>>? connect = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Flow> _flows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private volatile bool _authenticated;

    public int ActiveFlowCount => _flows.Count;

    public void Authenticate() => _authenticated = true;

    public void Handle(EgressFrame frame)
    {
        if (!_authenticated || !Guid.TryParseExact(frame.FlowId, "N", out _) || frame.Data.Length > 65507)
            throw new InvalidDataException("The egress frame is not authorized or exceeds its bound.");
        if (frame.Operation == EgressFrame.Types.Operation.Open)
        {
            if (frame.Port is < 1 or > 65535 || frame.Data.Length != 0 || _flows.Count >= 128 ||
                _usedIds.Count >= 100_000 || !_usedIds.Add(frame.FlowId))
                throw new InvalidDataException("The egress flow request is invalid or exceeds its bound.");
            var flow = new Flow(frame.FlowId, frame.Udp, _stop.Token);
            if (!_flows.TryAdd(frame.FlowId, flow)) throw new InvalidDataException("Duplicate egress flow.");
            flow.Task = RunAsync(flow, frame);
        }
        else if (_flows.TryGetValue(frame.FlowId, out var flow))
        {
            if (frame.Operation == EgressFrame.Types.Operation.Close) flow.Cancel();
            else if (frame.Operation == EgressFrame.Types.Operation.Credit && frame.Data.Length == 0)
            {
                try { flow.OutputCredits.Release(); }
                catch (SemaphoreFullException) { throw new InvalidDataException("Duplicate egress credit."); }
            }
            else if (frame.Operation == EgressFrame.Types.Operation.HalfClose && !flow.Udp) flow.Input.Writer.TryComplete();
            else if (frame.Operation != EgressFrame.Types.Operation.Data || !flow.Input.Writer.TryWrite(frame.Data))
                flow.Cancel(); // bounded backpressure cannot stall the broker command loop
        }
        else if (frame.Operation != EgressFrame.Types.Operation.Close && !(_usedIds.Contains(frame.FlowId) &&
            frame.Operation is EgressFrame.Types.Operation.Credit or EgressFrame.Types.Operation.HalfClose or EgressFrame.Types.Operation.Data))
            throw new InvalidDataException("Unknown egress flow.");
    }

    private async Task RunAsync(Flow flow, EgressFrame request)
    {
        using var stop = flow.Stop;
        try
        {
            stop.CancelAfter(TimeSpan.FromSeconds(30));
            var address = await policy.ResolveAsync(request.Address, stop.Token);
            using var socket = connect is null
                ? await ConnectAsync(address, request.Port, flow.Udp, stop.Token)
                : await connect(address, request.Port, flow.Udp, stop.Token);
            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Opened }, stop.Token);
            stop.CancelAfter(flow.Idle);
            var upload = UploadAsync(socket, flow);
            var download = DownloadAsync(socket, flow);
            var first = await Task.WhenAny(upload, download);
            await first;
            if (first == upload && upload.IsCompletedSuccessfully) await download;
            await stop.CancelAsync();
            try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { }
        }
        catch (Exception error) when (error is SocketException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            if (!_stop.IsCancellationRequested)
                await SafeSendAsync(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Error,
                    ErrorCode = error is UnauthorizedAccessException ? "network_destination_denied" :
                        error is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain } ? "network_dns_failed" : "network_transport_failed" });
        }
        finally
        {
            await stop.CancelAsync();
            _flows.TryRemove(flow.Id, out _);
            await SafeSendAsync(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Close });
        }
    }

    private static async Task<Socket> ConnectAsync(System.Net.IPAddress address, int port, bool udp, CancellationToken token)
    {
        var socket = new Socket(address.AddressFamily, udp ? SocketType.Dgram : SocketType.Stream,
            udp ? ProtocolType.Udp : ProtocolType.Tcp);
        try { await socket.ConnectAsync(new System.Net.IPEndPoint(address, port), token); return socket; }
        catch { socket.Dispose(); throw; }
    }

    private async Task UploadAsync(Socket socket, Flow flow)
    {
        await foreach (var bytes in flow.Input.Reader.ReadAllAsync(flow.Stop.Token))
        {
            if (flow.Udp) await socket.SendAsync(bytes.Memory, SocketFlags.None, flow.Stop.Token);
            else
            {
                var remaining = bytes.Memory;
                while (remaining.Length > 0)
                {
                    var sent = await socket.SendAsync(remaining, SocketFlags.None, flow.Stop.Token);
                    if (sent == 0) throw new IOException("Egress socket closed.");
                    remaining = remaining[sent..];
                }
            }
            flow.Stop.CancelAfter(flow.Idle);
            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Credit }, flow.Stop.Token);
        }
        if (!flow.Udp) socket.Shutdown(SocketShutdown.Send);
    }

    private async Task DownloadAsync(Socket socket, Flow flow)
    {
        var buffer = new byte[65507];
        while (!flow.Stop.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None, flow.Stop.Token);
            if (read == 0 && !flow.Udp) return;
            flow.Stop.CancelAfter(flow.Idle);
            await flow.OutputCredits.WaitAsync(flow.Stop.Token);
            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Data,
                Data = ByteString.CopyFrom(buffer, 0, read) }, flow.Stop.Token);
        }
    }

    private async Task SafeSendAsync(EgressFrame frame)
    {
        try { await send(frame, _stop.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        var tasks = _flows.Values.Select(x => x.Task).ToArray();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }

    private sealed class Flow(string id, bool udp, CancellationToken token)
    {
        public string Id { get; } = id;
        public bool Udp { get; } = udp;
        public TimeSpan Idle => Udp ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(5);
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Channel<ByteString> Input { get; } = Channel.CreateBounded<ByteString>(2);
        public SemaphoreSlim OutputCredits { get; } = new(2, 2);
        public Task Task { get; set; } = Task.CompletedTask;
        public void Cancel()
        {
            try { Stop.Cancel(); }
            catch (ObjectDisposedException) { } // a terminal frame can race the flow's final removal
        }
    }
}
