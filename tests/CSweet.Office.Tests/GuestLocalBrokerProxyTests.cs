using System.Text;
using CSweet.Office.RuntimeGuest;

namespace CSweet.Office.Tests;

public sealed class GuestLocalBrokerProxyTests
{
    [Theory]
    [InlineData(false, 2578964)]
    [InlineData(true, 2578964)]
    [InlineData(false, GuestLocalBrokerProxy.MaximumBodyBytes)]
    [InlineData(true, GuestLocalBrokerProxy.MaximumBodyBytes)]
    public async Task ReadRequestAsync_AcceptsProductionSizedBodies(bool chunked, int size)
    {
        var body = new string('x', size);
        var wire = chunked
            ? $"POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n{size:x}\r\n{body}\r\n0\r\n\r\n"
            : $"POST /mcp HTTP/1.1\r\nContent-Length: {size}\r\n\r\n{body}";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));
        var request = await GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None);
        Assert.Equal(body, Encoding.ASCII.GetString(request.Body.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleRequestAsync_OversizedBodyReturns413WithoutForwarding(bool chunked)
    {
        var size = GuestLocalBrokerProxy.MaximumBodyBytes + 1;
        var wire = chunked
            ? $"POST /mcp HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n{size:x}\r\n"
            : $"POST /mcp HTTP/1.1\r\nContent-Length: {size}\r\n\r\n";
        await using var stream = new MemoryStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(wire));
        stream.Position = 0;
        var forwarded = false;
        await using var proxy = new GuestLocalBrokerProxy(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            (_, _) =>
            {
                forwarded = true;
                throw new InvalidOperationException("An oversized request must not be forwarded.");
            });
        await proxy.HandleRequestAsync(stream, CancellationToken.None);
        var response = Encoding.UTF8.GetString(stream.ToArray(), wire.Length, (int)stream.Length - wire.Length);
        Assert.StartsWith("HTTP/1.1 413 Content Too Large", response);
        Assert.Contains("exceeds the guest broker", response);
        Assert.False(forwarded);
    }

    [Fact]
    public async Task WriteResponseAsync_AcceptsProductionSizedResponse()
    {
        var body = new byte[2578964];
        await using var stream = new MemoryStream();
        await GuestLocalBrokerProxy.WriteResponseAsync(stream,
            new GuestLocalBrokerResponse(200, new Dictionary<string, string>(), body), CancellationToken.None);
        Assert.True(stream.Length > body.Length);
    }
    [Fact]
    public async Task ReadRequestAsync_AcceptsBoundedChunkedJsonContent()
    {
        const string body = "{\"jsonrpc\":\"2.0\"}";
        var first = body[..8];
        var second = body[8..];
        var wire =
            "POST /mcp HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            $"{first.Length:x}\r\n{first}\r\n" +
            $"{second.Length:x}\r\n{second}\r\n" +
            "0\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        var request = await GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal("POST", request.Method);
        Assert.Equal("/mcp", request.Path);
        Assert.Equal(body, Encoding.UTF8.GetString(request.Body.Span));
        Assert.DoesNotContain("Transfer-Encoding", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsConflictingBodyFraming()
    {
        const string wire =
            "POST /mcp HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Length: 0\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteResponseAsync_ReframesBufferedResponseWithoutHopByHopHeaders()
    {
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":{}}");
        var response = new GuestLocalBrokerResponse(
            200,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Transfer-Encoding"] = "chunked",
                ["Connection"] = "keep-alive",
                ["Keep-Alive"] = "timeout=5"
            },
            body);
        await using var stream = new MemoryStream();

        await GuestLocalBrokerProxy.WriteResponseAsync(stream, response, CancellationToken.None);

        var wire = Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains($"Content-Length: {body.Length}\r\n", wire, StringComparison.Ordinal);
        Assert.Contains("Connection: close\r\n", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Transfer-Encoding", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep-Alive", wire, StringComparison.OrdinalIgnoreCase);
    }

    private const string GitWorkspace = "/git/workspaces/0f8fad5b-d9cb-469f-a165-70867728950e/7";

    [Theory]
    [InlineData("git-upload-pack")]
    [InlineData("git-receive-pack")]
    public async Task ReadRequestAsync_AcceptsGitAdvertisementWithItsQueryAndNoBody(string service)
    {
        var path = $"{GitWorkspace}/info/refs?service={service}";
        var wire = $"GET {path} HTTP/1.1\r\nHost: localhost\r\nGit-Protocol: version=2\r\n\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        var request = await GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal("GET", request.Method);
        Assert.Equal(path, request.Path);
        Assert.True(request.Body.IsEmpty);
        Assert.Equal("version=2", request.Headers["Git-Protocol"]);
    }

    [Theory]
    [InlineData("git-upload-pack")]
    [InlineData("git-receive-pack")]
    public async Task ReadRequestAsync_AcceptsGitExchangeBody(string service)
    {
        var path = $"{GitWorkspace}/{service}";
        var wire = $"POST {path} HTTP/1.1\r\nContent-Length: 4\r\n\r\n0000";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        var request = await GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None);

        Assert.Equal("POST", request.Method);
        Assert.Equal(path, request.Path);
        Assert.Equal("0000", Encoding.ASCII.GetString(request.Body.Span));
    }

    [Theory]
    [InlineData("GET /mcp HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/info/refs HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/info/refs?service=git-upload-archive HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/info/refs?service=git-upload-pack&x=1 HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/git-upload-pack HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/HEAD HTTP/1.1\r\n\r\n")]
    [InlineData("GET /git/workspaces/not-a-workspace/7/info/refs?service=git-upload-pack HTTP/1.1\r\n\r\n")]
    [InlineData("GET /git/workspaces/0f8fad5b-d9cb-469f-a165-70867728950e/0/info/refs?service=git-upload-pack HTTP/1.1\r\n\r\n")]
    [InlineData("GET " + GitWorkspace + "/../7/info/refs?service=git-upload-pack HTTP/1.1\r\n\r\n")]
    [InlineData("POST " + GitWorkspace + "/info/refs?service=git-upload-pack HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("POST " + GitWorkspace + "/git-upload-pack?x=1 HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("PUT " + GitWorkspace + "/git-receive-pack HTTP/1.1\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("DELETE " + GitWorkspace + "/git-receive-pack HTTP/1.1\r\n\r\n")]
    public async Task ReadRequestAsync_RejectsEverythingOutsideTheFixedRoutes(string wire)
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("Content-Length: 4\r\n\r\n0000")]
    [InlineData("Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n")]
    public async Task ReadRequestAsync_RejectsGitAdvertisementWithABody(string rest)
    {
        var wire = $"GET {GitWorkspace}/info/refs?service=git-upload-pack HTTP/1.1\r\n{rest}";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(wire));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GuestLocalBrokerProxy.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("GET", "/info/refs?service=git-upload-pack")]
    [InlineData("GET", "/info/refs?service=git-receive-pack")]
    [InlineData("POST", "/git-upload-pack")]
    [InlineData("POST", "/git-receive-pack")]
    public void PurposeFor_GivesGitOnlyToAnAgentRuntime(string method, string suffix)
    {
        var path = GitWorkspace + suffix;

        Assert.Equal("git.runtime", GuestBrokerSession.PurposeFor(1, method, path));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(0, method, path));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(2, method, path));
    }

    [Fact]
    public void PurposeFor_KeepsTheExistingPurposesAndRefusesOtherMethods()
    {
        Assert.Equal("mcp.runtime", GuestBrokerSession.PurposeFor(1, "POST", "/mcp"));
        Assert.Equal("mcp.runtime", GuestBrokerSession.PurposeFor(2, "POST", "/mcp"));
        Assert.Equal("build.fetch", GuestBrokerSession.PurposeFor(0, "POST", "/build/fetch"));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(0, "POST", "/mcp"));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(1, "POST", "/build/fetch"));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(1, "GET", "/mcp"));
        Assert.Throws<UnauthorizedAccessException>(() => GuestBrokerSession.PurposeFor(1, "POST", GitWorkspace + "/info/refs?service=git-upload-pack"));
    }
}
