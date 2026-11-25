using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using CSweet.Office.Contracts.ControlPlane;
using QRCoder;

namespace CSweet.Office.Node.Desktop;

internal sealed class OfficeDesktop : IDisposable
{
    private static readonly Uri Account = new("https://account.c-sweet.com/");
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim gate = new(1);
    private readonly string store;
    private string ManagementFile => Path.Combine(Path.GetDirectoryName(store)!, "headquarters.json");
    private sealed record Management(Uri Headquarters, Uri Gateway);
    private RSA key = RSA.Create(2048);
    private OfficeLinkRequest? link;
    private Uri? destination;
    private bool managed;
    private string? token, pin;
    private Uri? gateway;
    private string? message;
    private Task<int>? installation;
    private DateTimeOffset nextCheck;
    private OfficeDesktop(string store) => this.store = store;

    private sealed record Saved(string PrivateKey, OfficeLinkRequest Link, Uri? Destination, bool Managed);
    private sealed record DirectoryReply(Uri Headquarters);
    internal sealed record Start(string Mode, string? Address);
    internal sealed record Setup(string? Gateway, string? Code, string? Pin, bool Consent, DesktopAllocation? Allocation = null);

    internal static Task RunAsync() => RunAsync(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSweet", "OfficeDesktop"), Open, CancellationToken.None);

    internal static async Task RunAsync(string root, Action<string> open, CancellationToken stop)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string launchFile = Path.Combine(root, "launch");
        FileStream owner;
        try { owner = new FileStream(Path.Combine(root, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException)
        {
            for (var i = 0; i < 20; i++)
            {
                if (File.Exists(launchFile) && Uri.TryCreate(await File.ReadAllTextAsync(launchFile), UriKind.Absolute, out var active) &&
                    active.Scheme == "http" && active.Host == "127.0.0.1" && active.Fragment.Length == 65)
                { open(active.AbsoluteUri); return; }
                await Task.Delay(100);
            }
            throw new IOException("Office is opening. Try again in a moment.");
        }
        using var localLock = owner;
        using var desktop = new OfficeDesktop(Path.Combine(root, "pending.json"));
        desktop.Restore();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxRequestBodySize = 4096; });
        await using var app = builder.Build();
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var lastAccess = Environment.TickCount64;
        string origin = "";
        app.Use(async (ctx, next) =>
        {
            if (origin.Length == 0 || ctx.Request.Host.Value != origin["http://".Length..] || ctx.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            { ctx.Response.StatusCode = 403; return; }
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                if (ctx.Request.Headers["X-Office-Session"] != ticket || ctx.Request.Headers.Origin.Count != 0 && ctx.Request.Headers.Origin != origin)
                { ctx.Response.StatusCode = 403; return; }
                Interlocked.Exchange(ref lastAccess, Environment.TickCount64);
            }
            await next();
        });
        foreach (var (route, file, type) in new[] { ("/", "index.html", "text/html"), ("/office.js", "office.js", "text/javascript"), ("/office.css", "office.css", "text/css") })
            app.MapGet(route, () => Results.Stream(typeof(OfficeDesktop).Assembly.GetManifestResourceStream("OfficeDesktop." + file)!, type));
        app.MapGet("/api/status", () => desktop.Locked(desktop.Status));
        app.MapPost("/api/start", (Start request) => desktop.Locked(() => desktop.Begin(request)));
        app.MapPost("/api/install", (Setup request) => desktop.Locked(() => desktop.Install(request)));
        app.MapPost("/api/cancel", () => desktop.Locked(() =>
        {
            if (desktop.installation is { IsCompleted: false }) return Task.FromResult(Results.Conflict());
            desktop.Clear(); desktop.message = null; return Task.FromResult(Results.NoContent());
        }));
        await app.StartAsync();
        try
        {
            origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var url = origin + "/#" + ticket;
            await File.WriteAllTextAsync(launchFile, url);
            open(url);
            using var stopping = stop.Register(app.Lifetime.StopApplication);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping) &&
                (Environment.TickCount64 - Interlocked.Read(ref lastAccess) < 900_000 || desktop.installation is { IsCompleted: false })) { }
        }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
        finally { File.Delete(launchFile); await app.StopAsync(); }
    }

    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private async Task<IResult> Locked(Func<Task<IResult>> action)
    {
        await gate.WaitAsync();
        try { return await action(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { return Results.BadRequest(new { error = e.Message }); }
        finally { gate.Release(); }
    }

    private async Task<IResult> Begin(Start request)
    {
        if (installation is { IsCompleted: false } || (await OfficeDesktopInstaller.ServicesAsync()).Any(s => s.State != "Awaiting setup"))
            return Results.Conflict();
        if (request.Mode is not ("hosted" or "private")) return Results.BadRequest();
        var target = request.Mode == "private" ? OfficeDesktopInstaller.HeadquartersAddress(request.Address ?? "") : null;
        Clear();
        key.Dispose(); key = RSA.Create(2048);
        managed = request.Mode == "hosted"; destination = target;
        link = new(Environment.MachineName, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds());
        Save(); message = null; return Results.NoContent();
    }

    private async Task<IResult> Status()
    {
        var services = await OfficeDesktopInstaller.ServicesAsync();
        if (installation?.IsCompleted == true)
        {
            try
            {
                var installed = await installation == 0;
                if (installed || services.Any(s => s.State != "Awaiting setup")) Clear();
                message = installed ? "Services installed. Check this computer in Headquarters." : "Setup did not finish. Check administrator approval, the gateway and virtualization before retrying.";
            }
            catch (Exception) { message = "The installer could not finish. Check the installed package and administrator permission."; }
            installation = null;
        }
        if (link != null && link.Until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { Clear(); message = "Your request expired. Start a new connection."; }
        if (installation == null && link != null && token == null && nextCheck <= DateTimeOffset.UtcNow && services.All(s => s.State == "Awaiting setup"))
        {
            nextCheck = DateTimeOffset.UtcNow.AddSeconds(5);
            try
            {
                if (managed && destination == null)
                {
                    using var response = await http.GetAsync(new Uri(Account, "office/link/destination/" + link.Id));
                    if (response.IsSuccessStatusCode)
                    {
                        var found = (await response.Content.ReadFromJsonAsync<DirectoryReply>())?.Headquarters;
                        if (found == null || !System.Text.RegularExpressions.Regex.IsMatch(found.IdnHost, @"^hq-[a-f0-9]{32}\.c-sweet\.app$") || !found.IsDefaultPort)
                            throw new InvalidDataException();
                        destination = OfficeDesktopInstaller.Origin(found.AbsoluteUri); Save();
                    }
                    else if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
                }
                if (destination != null)
                {
                    using var response = await http.GetAsync(new Uri(destination, "api/setup/office-links/" + link.Id));
                    if (response.IsSuccessStatusCode)
                    {
                        var grant = await response.Content.ReadFromJsonAsync<OfficeLinkGrant>() ?? throw new InvalidDataException();
                        var service = OfficeDesktopInstaller.Origin(grant.Gateway);
                        if (service.IsLoopback && (managed || !destination.IsLoopback))
                            throw new ArgumentException("Headquarters supplied a gateway on localhost. Ask its administrator for a gateway address reachable from this computer.");
                        var code = Encoding.UTF8.GetString(key.Decrypt(Convert.FromBase64String(grant.SealedToken), RSAEncryptionPadding.OaepSHA256));
                        if (!OfficeDesktopInstaller.ValidToken(code)) throw new InvalidDataException();
                        gateway = service; pin = grant.CertificateSha256; token = code;
                    }
                    else if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
                }
                message = null;
            }
            catch (ArgumentException e) { message = e.Message; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or CryptographicException or InvalidDataException or FormatException)
            { message = "Could not verify the connection. Check the address, network and HTTPS certificate. Office will try again."; }
        }
        var verify = link == null ? null : new Uri(managed ? Account : destination!, (managed ? "office/link?" : "api/setup/office-links/confirm?") + link.Query).AbsoluteUri;
        string? qr = null;
        if (verify != null && token == null)
        {
            using var data = QRCodeGenerator.GenerateQrCode(verify, QRCodeGenerator.ECCLevel.M);
            qr = Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(4));
        }
        var health = services.Any(s => s.State != "Awaiting setup") ? OfficeHealth.Read() : null;
        string? manage = null;
        if (health != null && File.Exists(ManagementFile))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Management>(File.ReadAllText(ManagementFile));
                if (saved != null && OfficeDesktopInstaller.Origin(saved.Headquarters.AbsoluteUri) == saved.Headquarters &&
                    saved.Gateway.Authority == health.Gateway)
                    manage = new Uri(saved.Headquarters, $"settings/offices/{health.OfficeId:D}").AbsoluteUri;
            }
            catch (Exception e) when (e is IOException or ArgumentException or JsonException) { }
        }
        return Results.Ok(new { machine = Environment.MachineName, version = typeof(OfficeDesktop).Assembly.GetName().Version?.ToString(3),
            health, manage,
            defaultCpu = Math.Max(1, Environment.ProcessorCount - 1), services, verify, qr, matchingCode = link?.MatchingCode, expires = link?.Until, headquarters = destination?.Authority,
            gateway = gateway?.Authority, approved = token != null, installing = installation != null, message });
    }

    private async Task<IResult> Install(Setup request)
    {
        if (!request.Consent || installation != null || (await OfficeDesktopInstaller.ServicesAsync()).Any(s => s.State != "Awaiting setup")) return Results.Conflict();
        if (link != null && link.Until <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { Clear(); return Results.BadRequest(); }
        var service = gateway ?? OfficeDesktopInstaller.Origin(request.Gateway ?? "");
        var code = token ?? request.Code ?? "";
        var fingerprint = token == null ? request.Pin?.Trim() : pin;
        if (!OfficeDesktopInstaller.ValidToken(code) || fingerprint is { Length: > 0 } && (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit)))
            return Results.BadRequest(new { error = "Check the one-use code and certificate fingerprint." });
        request.Allocation?.Validate();
        var proofInput = token == null || link == null ? null : FormattableString.Invariant(
            $"office1:{code}:{link.Until}:{Convert.ToBase64String(key.ExportPkcs8PrivateKey())}");
        if (destination != null) File.WriteAllText(ManagementFile, JsonSerializer.Serialize(new Management(destination, service)));
        installation = OfficeDesktopInstaller.InstallAsync(service, fingerprint, code, proofInput, request.Allocation);
        message = null;
        return Results.Accepted();
    }

    private void Restore()
    {
        if (!File.Exists(store)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(store));
            if (saved == null || !saved.Link.IsValid(DateTimeOffset.UtcNow)) { Clear(); return; }
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(saved.PrivateKey), out _);
            if (saved.Link.Key != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())) { Clear(); return; }
            link = saved.Link; destination = saved.Destination; managed = saved.Managed;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or IOException) { Clear(); }
    }
    private void Save()
    {
        var temp = store + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Saved(Convert.ToBase64String(key.ExportPkcs8PrivateKey()), link!, destination, managed)));
        File.Move(temp, store, true);
    }
    private void Clear() { File.Delete(store); link = null; gateway = null; destination = null; token = null; pin = null; nextCheck = default; }
    public void Dispose() { key.Dispose(); http.Dispose(); gate.Dispose(); }
}
