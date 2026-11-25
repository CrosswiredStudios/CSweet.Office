using System.Net;
using System.Net.Http.Json;
using CSweet.Office.Node.Desktop;

namespace CSweet.Office.Tests;

public class OfficeDesktopTests
{
    [Fact]
    public async Task LauncherServesTheLocalPageAndProtectsStatusAndSetup()
    {
        var directory = Directory.CreateTempSubdirectory();
        using var stop = new CancellationTokenSource();
        var launched = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = OfficeDesktop.RunAsync(directory.FullName, url => launched.SetResult(new Uri(url)), stop.Token);
        try
        {
            await Task.WhenAny(server, launched.Task).WaitAsync(TimeSpan.FromSeconds(20));
            if (server.IsCompleted) await server;
            var url = await launched.Task;
            Assert.Equal("127.0.0.1", url.Host);
            using var http = new HttpClient { BaseAddress = new Uri(url.GetLeftPart(UriPartial.Authority)) };
            var page = await http.GetStringAsync("/");
            Assert.Contains("C-Sweet hosts my Headquarters", page);
            Assert.Contains("I host my own Headquarters", page);
            Assert.DoesNotContain(url.Fragment[1..], page);
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/status")).StatusCode);
            http.DefaultRequestHeaders.Add("X-Office-Session", url.Fragment[1..]);
            http.DefaultRequestHeaders.Add("Origin", "https://other.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/cancel", new { })).StatusCode);
            http.DefaultRequestHeaders.Remove("Origin");
            var status = await http.GetStringAsync("/api/status");
            Assert.Contains(Environment.MachineName, status);
            Assert.DoesNotContain("PrivateKey", status);
            Assert.DoesNotContain("EnrollmentToken", status);
            http.DefaultRequestHeaders.Host = "wrong.example";
            Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/api/status")).StatusCode);
        }
        finally { stop.Cancel(); await server; directory.Delete(true); }
    }

    [Theory]
    [InlineData("192.168.10.4:8443", "https://192.168.10.4:8443/")]
    [InlineData("hq.studio.test", "https://hq.studio.test/")]
    [InlineData("[fd00::3]:8443", "https://[fd00::3]:8443/")]
    public void PrivateSetupAcceptsTheAddressPeopleUse(string address, string expected) =>
        Assert.Equal(expected, OfficeDesktopInstaller.HeadquartersAddress(address).AbsoluteUri);

    [Theory]
    [InlineData("http://hq.example")]
    [InlineData("https://user@hq.example")]
    [InlineData("https://hq.example/?token=test")]
    [InlineData("https://hq.example/other")]
    public void SetupRequiresACleanHttpsOrigin(string address) =>
        Assert.Throws<ArgumentException>(() => OfficeDesktopInstaller.Origin(address));
}
