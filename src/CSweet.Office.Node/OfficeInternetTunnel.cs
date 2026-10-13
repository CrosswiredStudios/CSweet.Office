using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Contracts.Guest;
using CSweet.Office.Contracts.Workloads;
using Google.Protobuf;
using Grpc.Core;

namespace CSweet.Office.Node;

internal static class OfficeInternetTunnel
{
    public static async Task RunAsync(OfficeGateway.OfficeGatewayClient client, Stream guest,
        OfficeState state, WorkloadAssignment assignment, RuntimeWorkloadSpecification workload,
        CancellationToken token)
    {
        var policy = workload.NetworkPolicy ?? throw new InvalidDataException("Missing signed egress policy.");
        policy.Validate();
        if (workload.BrokerLease.ProtocolVersion != "1.1") throw new InvalidDataException("Egress requires guest protocol 1.1.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var remainingLease = workload.BrokerLease.ExpiresAt - DateTimeOffset.UtcNow;
        if (remainingLease <= TimeSpan.Zero) throw new InvalidDataException("The guest egress authorization expired.");
        // Enforce the signed guest lease locally even if a guest holds its channel open.
        stop.CancelAfter(remainingLease);
        using var call = client.OpenWorkloadTunnel(cancellationToken: stop.Token);
        using var guestWriter = new SemaphoreSlim(1);
        async Task WriteGuestAsync(GuestEnvelope envelope, CancellationToken cancellation)
        {
            await guestWriter.WaitAsync(cancellation);
            try { await LengthDelimitedProtobuf.WriteAsync(guest, envelope, 16 * 1024 * 1024, cancellation); }
            finally { guestWriter.Release(); }
        }
        await using var egress = new RuntimeInternetEgress(new PublicInternetPolicy(policy.BlockedCidrs,
                allowIpv6: policy.Mode == RuntimeNetworkPolicy.PublicInternet),
            (frame, cancellation) => WriteGuestAsync(new GuestEnvelope { ProtocolVersion = "1.1",
                MessageId = Guid.NewGuid().ToString("N"), Egress = frame }, cancellation), stop.Token);
        long sequence = 0;
        WorkloadTunnelFrame Frame(ByteString content, bool completed = false) => new()
        {
            OfficeId = state.OfficeId.ToString("D"), AssignmentId = assignment.AssignmentId,
            FencingEpoch = assignment.FencingEpoch, SessionEpoch = state.SessionEpoch,
            Sequence = sequence++, Content = content, Completed = completed
        };
        // Preserve the required empty opening frame; Headquarters sends boot configuration first.
        await call.RequestStream.WriteAsync(Frame(ByteString.Empty), stop.Token);
        var upload = Task.Run(async () =>
        {
            while (true)
            {
                var envelope = await LengthDelimitedProtobuf.ReadAsync(guest, GuestEnvelope.Parser, 16 * 1024 * 1024, stop.Token);
                if (envelope is null) break;
                if (envelope.ProtocolVersion != "1.1" || !Guid.TryParseExact(envelope.MessageId, "N", out _))
                    throw new InvalidDataException("Invalid guest egress envelope.");
                if (envelope.BodyCase == GuestEnvelope.BodyOneofCase.Egress) { egress.Handle(envelope.Egress); continue; }
                using var bytes = new MemoryStream();
                await LengthDelimitedProtobuf.WriteAsync(bytes, envelope, 16 * 1024 * 1024, stop.Token);
                var payload = bytes.ToArray();
                for (var offset = 0; offset < payload.Length; offset += 65536)
                    await call.RequestStream.WriteAsync(Frame(ByteString.CopyFrom(payload, offset, Math.Min(65536, payload.Length - offset))), stop.Token);
            }
            await call.RequestStream.WriteAsync(Frame(ByteString.Empty, true), stop.Token);
            await call.RequestStream.CompleteAsync();
        }, stop.Token);
        var download = Task.Run(async () =>
        {
            using var stream = new TunnelInput(call.ResponseStream, assignment, state);
            while (true)
            {
                var envelope = await LengthDelimitedProtobuf.ReadAsync(stream, GuestEnvelope.Parser, 16 * 1024 * 1024, stop.Token);
                if (envelope is null) break;
                if (envelope.ProtocolVersion != "1.1" || !Guid.TryParseExact(envelope.MessageId, "N", out _))
                    throw new InvalidDataException("Invalid Headquarters guest envelope.");
                if (envelope.BodyCase == GuestEnvelope.BodyOneofCase.Lease && envelope.Lease.Accepted &&
                    envelope.Lease.ExpiresAtUnixSeconds == workload.BrokerLease.ExpiresAt.ToUnixTimeSeconds()) egress.Authenticate();
                if (envelope.BodyCase == GuestEnvelope.BodyOneofCase.Egress)
                    throw new InvalidDataException("Headquarters cannot supply Office egress traffic.");
                await WriteGuestAsync(envelope, stop.Token);
            }
        }, stop.Token);
        try { await await Task.WhenAny(upload, download); }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(upload, download); } catch (Exception e) when (e is OperationCanceledException or RpcException or IOException) { }
        }
    }

    private sealed class TunnelInput(IAsyncStreamReader<WorkloadTunnelFrame> reader,
        WorkloadAssignment assignment, OfficeState state) : Stream
    {
        private ReadOnlyMemory<byte> _remaining;
        private long _sequence;
        private bool _completed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_remaining.Length == 0)
            {
                if (_completed || !await reader.MoveNext(cancellationToken)) return 0;
                var frame = reader.Current;
                if (frame.Sequence != _sequence++ || frame.FencingEpoch != assignment.FencingEpoch ||
                    frame.AssignmentId != assignment.AssignmentId || frame.OfficeId != state.OfficeId.ToString("D") ||
                    frame.SessionEpoch != state.SessionEpoch || frame.Content.Length > 65536)
                    throw new InvalidDataException("Invalid Headquarters tunnel binding or sequence.");
                _remaining = frame.Content.Memory;
                _completed = frame.Completed;
            }
            var count = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..count].CopyTo(buffer); _remaining = _remaining[count..]; return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
