using System.Net;
using System.Net.Sockets;
using Google.Protobuf;

namespace CSweet.Office.Tests;

public sealed class CertificationBrokerHostTests
{
    [Fact]
    public async Task AuthenticatedDiagnosticsDoNotInterruptReportOrSuccessfulExit()
    {
        await using var guest = await Session.StartAsync();
        await guest.WriteAsync(Log(0, "npm warn fixture"));
        await guest.WriteAsync(Log(1, new string('x', 9000) + "latest diagnostic"));
        await guest.WriteAsync(new GuestEnvelope
        {
            ProxyRequest = new ProxyRequest { RequestId = Guid.NewGuid().ToString("N"), Purpose = "mcp.runtime",
                Method = "POST", Path = "/mcp", Body = ByteString.CopyFromUtf8("certification report") }
        });
        var response = await guest.ReadAsync();
        Assert.Equal(200, response.ProxyResponse.StatusCode);
        await guest.WriteAsync(new GuestEnvelope { Exit = new GuestExit { ExitCode = 0 } });
        await guest.Run;
        Assert.Equal(8192, guest.Host.DiagnosticDetail!.Length);
        Assert.EndsWith("latest diagnostic", guest.Host.DiagnosticDetail);
        Assert.Equal(1, guest.Handler.Reports);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("sequence")]
    [InlineData("size")]
    [InlineData("completed")]
    [InlineData("digest")]
    [InlineData("protocol")]
    [InlineData("message-id")]
    public async Task MalformedDiagnosticsRemainRejected(string malformed)
    {
        await using var guest = await Session.StartAsync();
        var message = Log(0, "diagnostic");
        switch (malformed)
        {
            case "stream": message.StreamChunk.StreamId = "build.artifact"; break;
            case "sequence": message.StreamChunk.Sequence = -1; break;
            case "size": message.StreamChunk.Content = ByteString.CopyFrom(new byte[32769]); break;
            case "completed": message.StreamChunk.Completed = true; break;
            case "digest": message.StreamChunk.Digest = "sha256:" + new string('a', 64); break;
        }
        await guest.WriteAsync(message, malformed == "protocol" ? "1.0" : null,
            malformed == "message-id" ? "invalid" : null);
        await Assert.ThrowsAsync<InvalidDataException>(() => guest.Run);
        Assert.Equal(0, guest.Handler.Reports);
    }

    [Fact]
    public async Task ReplayedDiagnosticSequenceIsRejected()
    {
        await using var guest = await Session.StartAsync();
        await guest.WriteAsync(Log(0, "first"));
        await guest.WriteAsync(Log(0, "replay"));
        await Assert.ThrowsAsync<InvalidDataException>(() => guest.Run);
    }

    [Fact]
    public async Task DiagnosticsCannotReplaceCompletionOrConvertFailedExitToSuccess()
    {
        await using var guest = await Session.StartAsync();
        await guest.WriteAsync(Log(0, "all checks passed"));
        Assert.False(guest.Run.IsCompleted);
        await guest.WriteAsync(new GuestEnvelope { Exit = new GuestExit { ExitCode = 2, ReasonCode = "probe-failed" } });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => guest.Run);
        Assert.Contains("probe-failed", failure.Message);
        Assert.Equal(0, guest.Handler.Reports);
    }

    private static GuestEnvelope Log(long sequence, string content) => new()
    {
        StreamChunk = new StreamChunk { StreamId = "runtime.logs", Sequence = sequence,
            Content = ByteString.CopyFromUtf8(content) }
    };

    private sealed class ReportHandler : IAgentBrokerOperationHandler
    {
        public int Reports { get; private set; }
        public Task<BrokerOperationResult> HandleAsync(BrokerOperationContext request, CancellationToken cancellationToken)
        {
            Reports++;
            return Task.FromResult(new BrokerOperationResult(200, new Dictionary<string, string>(), "ok"u8.ToArray()));
        }
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient _guest;
        private readonly TcpClient _host;
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(10));
        public ReportHandler Handler { get; } = new();
        public GuestBrokerHostSession Host { get; }
        public Task Run { get; }
        private readonly AgentBrokerGrant _grant = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "sha256:" + new string('a', 64), "sha256:" + new string('b', 64), "1.1",
            "certification-test-boot-token", DateTimeOffset.UtcNow.AddMinutes(1),
            new HashSet<string> { "mcp.runtime" }, 16, 1024 * 1024, 1024 * 1024, 1024 * 1024);

        private Session(TcpClient guest, TcpClient host)
        {
            _guest = guest; _host = host;
            Host = new GuestBrokerHostSession(_grant, Handler, TimeProvider.System,
                new GuestBootConfiguration(), new StartCommand { WorkloadKind = 1, MaximumLogBytes = 1024 * 1024 });
            Run = Host.RunAsync(host.GetStream(), host.GetStream(), _stop.Token);
        }

        public static async Task<Session> StartAsync()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            var session = new Session(client, await listener.AcceptTcpClientAsync());
            await session.ReadAsync(); // Boot configuration precedes the authenticated hello.
            var grant = session._grant;
            using var handshake = new GuestHandshakeClient(new ExpectedGuestIdentity(grant.WorkloadId, grant.ChannelId,
                grant.GuestImageDigest, grant.ArtifactDigest, grant.BootToken, grant.ExpiresAt, grant.ProtocolVersion), TimeProvider.System);
            await session.WriteAsync(new GuestEnvelope { Hello = handshake.CreateHello() });
            var challenge = await session.ReadAsync();
            await session.WriteAsync(new GuestEnvelope { Proof = handshake.Answer(challenge.Challenge) });
            Assert.True((await session.ReadAsync()).Lease.Accepted);
            Assert.Equal(GuestEnvelope.BodyOneofCase.StartCommand, (await session.ReadAsync()).BodyCase);
            return session;
        }

        public Task WriteAsync(GuestEnvelope message, string? protocol = null, string? messageId = null)
        {
            message.ProtocolVersion = protocol ?? _grant.ProtocolVersion;
            message.MessageId = messageId ?? Guid.NewGuid().ToString("N");
            return LengthDelimitedProtobuf.WriteAsync(_guest.GetStream(), message, _grant.MaximumFrameBytes, _stop.Token);
        }

        public async Task<GuestEnvelope> ReadAsync() => await LengthDelimitedProtobuf.ReadAsync(_guest.GetStream(),
            GuestEnvelope.Parser, _grant.MaximumFrameBytes, _stop.Token) ?? throw new EndOfStreamException();

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try { await Run; } catch { }
            _guest.Dispose(); _host.Dispose(); _stop.Dispose();
        }
    }
}
