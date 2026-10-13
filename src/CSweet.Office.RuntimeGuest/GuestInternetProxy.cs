using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using CSweet.Office.Contracts.Guest;
using Google.Protobuf;

namespace CSweet.Office.RuntimeGuest;

/// <summary>Local SOCKS5 endpoint for the fixed TUN adapter. All authority is enforced in Office.</summary>
internal sealed class GuestInternetProxy(Func<EgressFrame, CancellationToken, Task> send,
    CancellationToken lifetime) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Flow> _flows = new();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private Task? _accept;
    private Process? _tun;
    private int _clientId;

    public async Task StartAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Runtime egress requires the certified Linux guest.");
        _listener.Start(16);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        // Fixed privileged setup owned by the guest service; no agent-selected commands or paths.
        await IpAsync("tuntap", "add", "dev", "csweet0", "mode", "tun");
        await IpAsync("addr", "add", "198.18.0.1/15", "dev", "csweet0");
        await IpAsync("-6", "addr", "add", "fd00:c5ee::1/64", "dev", "csweet0");
        await IpAsync("link", "set", "dev", "csweet0", "up");
        await IpAsync("route", "add", "default", "dev", "csweet0");
        await IpAsync("-6", "route", "add", "default", "dev", "csweet0");
        await File.WriteAllTextAsync("/run/csweet/resolv.conf", "nameserver 1.1.1.1\nnameserver 8.8.8.8\n", _stop.Token);
        var start = new ProcessStartInfo("/usr/lib/csweet/tun2socks") { UseShellExecute = false };
        start.ArgumentList.Add("--device"); start.ArgumentList.Add("tun://csweet0");
        start.ArgumentList.Add("--proxy"); start.ArgumentList.Add($"socks5://127.0.0.1:{port}");
        start.ArgumentList.Add("--loglevel"); start.ArgumentList.Add("error");
        _tun = Process.Start(start) ?? throw new IOException("The certified TUN adapter did not start.");
        _accept = AcceptAsync();
    }

    private async Task IpAsync(params string[] args)
    {
        var start = new ProcessStartInfo("/usr/sbin/ip") { UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Guest network setup failed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new IOException("Guest network setup failed.");
    }

    public void Handle(EgressFrame frame)
    {
        if (frame.Data.Length > 65507 || !_flows.TryGetValue(frame.FlowId, out var flow)) return;
        if (frame.Operation == EgressFrame.Types.Operation.Opened) flow.Opened.TrySetResult();
        else if (frame.Operation == EgressFrame.Types.Operation.Credit && frame.Data.Length == 0)
        {
            try { flow.OutputCredits.Release(); }
            catch (SemaphoreFullException) { flow.Input.Writer.TryComplete(new IOException("Duplicate egress credit.")); }
        }
        else if (frame.Operation == EgressFrame.Types.Operation.Data)
        {
            if (!flow.Input.Writer.TryWrite(frame.Data)) flow.Input.Writer.TryComplete(new IOException("Egress receive bound exceeded."));
        }
        else
        {
            flow.Opened.TrySetException(new IOException(frame.ErrorCode.Length > 0 ? frame.ErrorCode : "network_transport_failed"));
            flow.Input.Writer.TryComplete(frame.Operation == EgressFrame.Types.Operation.Error ? new IOException(frame.ErrorCode) : null);
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            if (_clients.Count >= 128) { client.Dispose(); continue; }
            var id = Interlocked.Increment(ref _clientId);
            var task = ClientAsync(client);
            _clients[id] = task;
            _ = task.ContinueWith(_ => _clients.TryRemove(id, out var ignored), TaskScheduler.Default);
        }
    }

    private async Task ClientAsync(TcpClient client)
    {
        using (client)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            stop.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                var stream = client.GetStream();
                var header = new byte[2]; await stream.ReadExactlyAsync(header, stop.Token);
                if (header[0] != 5 || header[1] == 0) return;
                var methods = new byte[header[1]]; await stream.ReadExactlyAsync(methods, stop.Token);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] { 5, 255 }, stop.Token); return; }
                await stream.WriteAsync(new byte[] { 5, 0 }, stop.Token);
                var request = new byte[3]; await stream.ReadExactlyAsync(request, stop.Token);
                if (request[0] != 5 || request[2] != 0 || request[1] is not (1 or 3)) return;
                var destination = await DestinationAsync(stream, stop.Token);
                if (request[1] == 3) { await UdpAsync(stream, stop.Token); return; }
                var flow = await OpenAsync(destination.Host, destination.Port, false, stop.Token);
                try
                {
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, stop.Token);
                    var upload = Task.Run(async () =>
                    {
                        var buffer = new byte[32768];
                        while (true)
                        {
                            var count = await stream.ReadAsync(buffer, stop.Token);
                            if (count == 0) { await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.HalfClose }, stop.Token); break; }
                            await flow.OutputCredits.WaitAsync(stop.Token);
                            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Data,
                                Data = ByteString.CopyFrom(buffer, 0, count) }, stop.Token);
                            stop.CancelAfter(TimeSpan.FromMinutes(5));
                        }
                    }, stop.Token);
                    var download = Task.Run(async () =>
                    {
                        await foreach (var data in flow.Input.Reader.ReadAllAsync(stop.Token))
                        {
                            await stream.WriteAsync(data.Memory, stop.Token);
                            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Credit }, stop.Token);
                            stop.CancelAfter(TimeSpan.FromMinutes(5));
                        }
                    }, stop.Token);
                    try { var first = await Task.WhenAny(upload, download); await first; if (first == upload && upload.IsCompletedSuccessfully) await download; }
                    finally { await stop.CancelAsync(); try { await Task.WhenAll(upload, download); } catch (OperationCanceledException) { } }
                }
                finally { _flows.TryRemove(flow.Id, out _); await CloseAsync(flow.Id); }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or TimeoutException) { }
        }
    }

    private async Task UdpAsync(NetworkStream control, CancellationToken token)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        await control.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var flows = new Dictionary<(string Host, int Port), (Flow Flow, byte[] Header, Task Reply)>();
        IPEndPoint? peer = null;
        var reader = Task.Run(async () =>
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(stop.Token);
                if (!IPAddress.IsLoopback(packet.RemoteEndPoint.Address) || packet.Buffer.Length < 4 ||
                    packet.Buffer[0] != 0 || packet.Buffer[1] != 0 || packet.Buffer[2] != 0) continue;
                peer ??= packet.RemoteEndPoint;
                if (!peer.Equals(packet.RemoteEndPoint)) continue;
                using var bytes = new MemoryStream(packet.Buffer, 3, packet.Buffer.Length - 3);
                var target = await DestinationAsync(bytes, stop.Token);
                if (flows.TryGetValue(target, out var previous) && previous.Flow.Input.Reader.Completion.IsCompleted)
                {
                    flows.Remove(target);
                    _flows.TryRemove(previous.Flow.Id, out _);
                    await CloseAsync(previous.Flow.Id);
                    try { await previous.Reply; } catch (Exception e) when (e is IOException or SocketException) { }
                }
                if (!flows.TryGetValue(target, out var entry))
                {
                    if (flows.Count >= 128) continue;
                    var flow = await OpenAsync(target.Host, target.Port, true, stop.Token);
                    var prefix = packet.Buffer[..(3 + (int)bytes.Position)];
                    var reply = Task.Run(async () =>
                    {
                        await foreach (var data in flow.Input.Reader.ReadAllAsync(stop.Token))
                        {
                            await udp.SendAsync(prefix.Concat(data.ToByteArray()).ToArray(), peer, stop.Token);
                            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Credit }, stop.Token);
                        }
                    }, stop.Token);
                    entry = (flow, prefix, reply); flows.Add(target, entry);
                }
                var offset = 3 + (int)bytes.Position;
                await entry.Flow.OutputCredits.WaitAsync(stop.Token);
                await send(new EgressFrame { FlowId = entry.Flow.Id, Operation = EgressFrame.Types.Operation.Data,
                    Data = ByteString.CopyFrom(packet.Buffer, offset, packet.Buffer.Length - offset) }, stop.Token);
            }
        }, stop.Token);
        var closed = control.ReadAsync(new byte[1], stop.Token).AsTask();
        try { await await Task.WhenAny(reader, closed); }
        finally
        {
            await stop.CancelAsync();
            try { await reader; } catch (Exception e) when (e is OperationCanceledException or IOException or SocketException) { }
            foreach (var entry in flows.Values)
            {
                _flows.TryRemove(entry.Flow.Id, out _); await CloseAsync(entry.Flow.Id);
                try { await entry.Reply; } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            }
        }
    }

    private async Task<Flow> OpenAsync(string host, int port, bool udp, CancellationToken token)
    {
        if (_flows.Count >= 128) throw new IOException("Egress flow bound exceeded.");
        var flow = new Flow(Guid.NewGuid().ToString("N")); _flows[flow.Id] = flow;
        try
        {
            await send(new EgressFrame { FlowId = flow.Id, Operation = EgressFrame.Types.Operation.Open,
                Address = host, Port = port, Udp = udp }, token);
            await flow.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), token); return flow;
        }
        catch { _flows.TryRemove(flow.Id, out _); await CloseAsync(flow.Id); throw; }
    }

    private async Task CloseAsync(string id)
    {
        try { await send(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Close }, _stop.Token); }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
    }

    internal static async Task<(string Host, int Port)> DestinationAsync(Stream stream, CancellationToken token)
    {
        var type = new byte[1]; await stream.ReadExactlyAsync(type, token);
        int length;
        if (type[0] == 1) length = 4;
        else if (type[0] == 4) length = 16;
        else if (type[0] == 3) { await stream.ReadExactlyAsync(type, token); length = type[0]; type[0] = 3; }
        else throw new InvalidDataException("Unsupported SOCKS address.");
        var address = new byte[length]; await stream.ReadExactlyAsync(address, token);
        var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
        return (type[0] == 3 ? System.Text.Encoding.ASCII.GetString(address) : new IPAddress(address).ToString(), (port[0] << 8) | port[1]);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop();
        if (_tun is { HasExited: false }) _tun.Kill(true);
        _tun?.Dispose();
        if (_accept is not null) try { await _accept; } catch (Exception e) when (e is OperationCanceledException or SocketException) { }
        try { await Task.WhenAll(_clients.Values); } catch (OperationCanceledException) { }
        foreach (var flow in _flows.Values) { flow.Opened.TrySetCanceled(); flow.Input.Writer.TryComplete(); }
        _stop.Dispose();
    }

    private sealed class Flow(string id)
    {
        public string Id { get; } = id;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ByteString> Input { get; } = Channel.CreateBounded<ByteString>(2);
        public SemaphoreSlim OutputCredits { get; } = new(2, 2);
    }
}
