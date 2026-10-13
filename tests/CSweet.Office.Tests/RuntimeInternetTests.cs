using System.Net;
using System.Net.Sockets;
using CSweet.Office.Node;
using CSweet.Office.Contracts.Guest;
using CSweet.Office.Runtime.Core;
using Google.Protobuf;
using System.Threading.Channels;

namespace CSweet.Office.Tests;

public sealed class RuntimeInternetTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("172.16.1.2")]
    [InlineData("192.168.1.1")]
    [InlineData("100.100.100.200")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("64:ff9b::a01:203")]
    public void NonPublicDestinationsAreDenied(string address) =>
        Assert.False(new PublicInternetPolicy().IsAllowed(IPAddress.Parse(address), []));

    [Fact]
    public async Task Ipv4PolicyDeniesNativeIpv6ButRetainsPublicMappedIpv4()
    {
        var policy = new PublicInternetPolicy(allowIpv6: false);
        Assert.False(policy.IsAllowed(IPAddress.Parse("2606:4700:4700::1111"), []));
        Assert.True(policy.IsAllowed(IPAddress.Parse("::ffff:8.8.8.8"), []));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => policy.ResolveAsync("2606:4700:4700::1111", default));
        Assert.Equal(IPAddress.Parse("8.8.8.8"), await policy.ResolveAsync("8.8.8.8", default));
    }

    [Fact]
    public void PublicAddressesRemainSubjectToHostAndAdministrativeBlocks()
    {
        var ip = IPAddress.Parse("8.8.8.8");
        Assert.True(new PublicInternetPolicy().IsAllowed(ip, []));
        Assert.True(new PublicInternetPolicy().IsAllowed(IPAddress.Parse("2606:4700:4700::1111"), []));
        Assert.False(new PublicInternetPolicy().IsAllowed(ip, [ip]));
        Assert.False(new PublicInternetPolicy(["8.8.8.0/24"]).IsAllowed(ip, []));
        Assert.False(new PublicInternetPolicy(["::ffff:8.8.8.0/120"]).IsAllowed(IPAddress.Parse("::ffff:8.8.8.8"), []));
        Assert.False(new PublicInternetPolicy(["::/80"]).IsAllowed(IPAddress.Parse("::ffff:8.8.8.8"), []));
        Assert.Throws<FormatException>(() => new PublicInternetPolicy(["invalid"]));
    }

    [Fact]
    public async Task EgressRequiresAuthenticationAndRejectsPrivateSocketConnections()
    {
        var output = Channel.CreateUnbounded<EgressFrame>();
        await using var egress = new RuntimeInternetEgress(new PublicInternetPolicy(),
            (frame, _) => { output.Writer.TryWrite(frame); return Task.CompletedTask; }, CancellationToken.None);
        var request = new EgressFrame { FlowId = Guid.NewGuid().ToString("N"), Port = 443, Address = "127.0.0.1" };
        Assert.Throws<InvalidDataException>(() => egress.Handle(request));
        egress.Authenticate(); egress.Handle(request);
        var rejection = await output.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(EgressFrame.Types.Operation.Error, rejection.Operation);
        Assert.Equal("network_destination_denied", rejection.ErrorCode);
        Assert.Throws<InvalidDataException>(() => egress.Handle(request));
        Assert.Throws<InvalidDataException>(() => egress.Handle(new EgressFrame { FlowId = Guid.NewGuid().ToString("N"),
            Operation = EgressFrame.Types.Operation.Data, Data = ByteString.CopyFrom(new byte[65508]) }));
    }

    [Fact]
    public async Task SustainedDownloadWaitsForCreditsAndPreservesAllBytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        var output = Channel.CreateUnbounded<EgressFrame>();
        await using var egress = new RuntimeInternetEgress(new PublicInternetPolicy(),
            (frame, _) => { output.Writer.TryWrite(frame); return Task.CompletedTask; }, timeout.Token,
            async (address, port, udp, token) =>
            {
                Assert.Equal(IPAddress.Parse("8.8.8.8"), address);
                Assert.False(udp);
                return await listener.AcceptSocketAsync(token);
            });
        var id = Guid.NewGuid().ToString("N");
        egress.Authenticate();
        egress.Handle(new EgressFrame { FlowId = id, Address = "8.8.8.8", Port = 443 });
        Assert.Equal(EgressFrame.Types.Operation.Opened, (await output.Reader.ReadAsync(timeout.Token)).Operation);
        var payload = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        var producer = peer.GetStream().WriteAsync(payload, timeout.Token).AsTask();
        using var received = new MemoryStream();
        for (var i = 0; i < 2; i++)
        {
            var frame = await output.Reader.ReadAsync(timeout.Token);
            Assert.Equal(EgressFrame.Types.Operation.Data, frame.Operation);
            received.Write(frame.Data.Span);
        }
        var next = output.Reader.ReadAsync(timeout.Token).AsTask();
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(100, timeout.Token)));
        egress.Handle(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Credit });
        while (received.Length < payload.Length)
        {
            var frame = await next;
            Assert.Equal(EgressFrame.Types.Operation.Data, frame.Operation);
            received.Write(frame.Data.Span);
            egress.Handle(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Credit });
            if (received.Length < payload.Length) next = output.Reader.ReadAsync(timeout.Token).AsTask();
        }
        await producer;
        Assert.Equal(payload, received.ToArray());
        egress.Handle(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Close });
        EgressFrame terminal;
        do { terminal = await output.Reader.ReadAsync(timeout.Token); } while (terminal.Operation != EgressFrame.Types.Operation.Close);
        Assert.Equal(0, egress.ActiveFlowCount);
        egress.Handle(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Credit });
        egress.Handle(new EgressFrame { FlowId = id, Operation = EgressFrame.Types.Operation.Close });
    }

    [Fact]
    public void InternetCertificationNeedsEveryRealProviderCheck()
    {
        var receipt = new ProviderCertificationEvidence("hyperv-gen2", "1.0.0", "windows", "x64", "sha256:image",
            "1.1", "csweet-hardware-vm-smoke-v15", DateTimeOffset.UtcNow, null);
        Assert.False(receipt.SupportsPublicInternet);
        var checks = ProviderCertificationEvidence.RequiredInternetChecks.ToDictionary(x => x, _ => true);
        Assert.True((receipt with { Checks = checks }).SupportsPublicInternet);
        checks["publicInternetHostDenied"] = false;
        Assert.False((receipt with { Checks = checks }).SupportsPublicInternet);
        Assert.False((receipt with { BrokerProtocolVersion = "1.0", Checks = checks }).SupportsPublicInternet);
        checks["publicInternetHostDenied"] = true;
        checks["publicInternetIpv6"] = false;
        var limited = receipt with { CertificationSuiteVersion = ProviderCertificationEvidence.Ipv4InternetSuite, Checks = checks };
        Assert.True(limited.SupportsPublicInternet);
        Assert.False(limited.SupportsPublicInternetIpv6);
        Assert.False((limited with { CertificationSuiteVersion = ProviderCertificationEvidence.FullInternetSuite }).SupportsPublicInternet);
    }
}
