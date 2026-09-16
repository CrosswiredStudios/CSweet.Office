using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

return await ToolchainGuestProgram.RunAsync(args);

internal static class ToolchainGuestProgram
{
    private static readonly DateTimeOffset NormalizedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] != "--adapter-entrypoint")
                throw new InvalidDataException("The toolchain guest requires one immutable adapter entrypoint.");
            var adapter = Path.GetFullPath(args[1]);
            if (!File.Exists(adapter)) throw new FileNotFoundException("The immutable toolchain adapter is missing.");
            var inputRoot = RequiredPath("CSWEET_BUILD_INPUT_ROOT");
            var outputRoot = RequiredPath("CSWEET_BUILD_OUTPUT_ROOT");
            var maximumSourceBytes = RequiredLong("CSWEET_BUILD_MAXIMUM_SOURCE_BYTES", 1, 2L * 1024 * 1024 * 1024);
            var maximumOutputBytes = RequiredLong("CSWEET_BUILD_MAXIMUM_OUTPUT_BYTES", 1, 10L * 1024 * 1024 * 1024);
            var repository = new Uri(Required("CSWEET_BUILD_SOURCE_URL"), UriKind.Absolute);
            var commit = Required("CSWEET_BUILD_SOURCE_COMMIT");
            if (commit.Length != 40 || commit.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("The exact source commit is invalid.");

            await using var broker = new ToolchainBrokerClient(
                Environment.GetEnvironmentVariable("CSweet__Agent__McpUnixSocketPath") ?? "/run/csweet/broker.sock");
            ResetDirectory(inputRoot);
            ResetDirectory(outputRoot);
            var fixtureResource = Environment.GetEnvironmentVariable("CSWEET_CERTIFICATION_FIXTURE_RESOURCE");
            if (string.IsNullOrWhiteSpace(fixtureResource))
            {
                var archivePath = Path.Combine(Path.GetDirectoryName(inputRoot)!, "source.zip");
                var archiveLayout = await broker.DownloadAsync(
                    SourceArchiveUri(repository, commit, Environment.GetEnvironmentVariable("CSWEET_BUILD_SOURCE_ARCHIVE_URL"),
                        Environment.GetEnvironmentVariable("CSWEET_BUILD_ID")), archivePath, maximumSourceBytes);
                ExtractSourceArchive(
                    archivePath, inputRoot, maximumSourceBytes,
                    flatLayout: string.Equals(archiveLayout, "flat", StringComparison.Ordinal));
                File.Delete(archivePath);
            }
            else
            {
                ValidateRelative(fixtureResource.Replace('\\', '/'));
                var artifactRoot = Path.GetDirectoryName(adapter)!;
                var fixtureRoot = ResolveBeneath(artifactRoot, fixtureResource.Replace('\\', '/'));
                CopyFixture(fixtureRoot, inputRoot, maximumSourceBytes);
            }

            var start = new ProcessStartInfo(adapter)
            {
                WorkingDirectory = Path.GetDirectoryName(adapter)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };
            using var process = Process.Start(start) ?? throw new IOException("The immutable toolchain adapter did not start.");
            var stdout = CopyOutputAsync(process.StandardOutput, Console.Out);
            var stderr = CopyOutputAsync(process.StandardError, Console.Error);
            var upload = UploadWhenReadyAsync(process, broker, outputRoot, maximumOutputBytes);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) return process.ExitCode;
            await upload;
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(Sanitize(exception.Message));
            return 1;
        }
    }

    internal static Uri SourceArchiveUri(Uri repository, string commit, string? trustedArchive = null, string? buildIdentity = null)
    {
        if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            throw new InvalidDataException("The exact source commit is invalid.");
        if (!string.IsNullOrEmpty(trustedArchive))
        {
            if (!Guid.TryParse(buildIdentity, out var build) || build == Guid.Empty ||
                trustedArchive != $"csweet-source://build/{build:N}/{commit}")
                throw new InvalidDataException("The trusted source archive must match this exact build and commit.");
            return new Uri(trustedArchive);
        }
        if (repository.Scheme != Uri.UriSchemeHttps || !repository.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Certified source preparation currently supports GitHub HTTPS repositories.");
        var segments = repository.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2) throw new InvalidDataException("The GitHub repository identity is invalid.");
        var name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return new Uri($"https://codeload.github.com/{Uri.EscapeDataString(segments[0])}/{Uri.EscapeDataString(name)}/zip/{commit}");
    }

    internal static void ExtractSourceArchive(
        string archivePath,
        string destination,
        long maximumBytes,
        bool flatLayout = false)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 1 or > 100_000) throw new InvalidDataException("The source archive entry count is invalid.");
        var roots = archive.Entries.Select(e => e.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (!flatLayout && roots.Length != 1)
            throw new InvalidDataException("The provider source archive must contain one repository root.");
        var prefix = flatLayout ? string.Empty : roots[0]! + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!flatLayout && (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)) continue;
            var relative = (flatLayout ? name : name[prefix.Length..]).TrimEnd('/');
            ValidateRelative(relative);
            if (!seen.Add(relative)) throw new InvalidDataException("The source archive contains duplicate or case-colliding paths.");
            var target = ResolveBeneath(destination, relative);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            total = checked(total + entry.Length);
            if (total > maximumBytes) throw new InvalidDataException("The expanded source exceeds its approved limit.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static async Task CreateOutputBundleAsync(string outputRoot, string bundlePath, long maximumBytes)
    {
        var files = Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith(".csweet-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        if (files.Length is < 1 or > 100_000) throw new InvalidDataException("The toolchain output file count is invalid.");
        long expanded = 0;
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) throw new InvalidDataException("The toolchain output contains a symbolic link.");
            expanded = checked(expanded + info.Length);
            if (expanded > maximumBytes) throw new InvalidDataException("The toolchain output exceeds its approved limit.");
        }
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = "1.0",
            operatingSystem = "linux",
            architecture,
            entrypoint = new[] { "run-output" }
        });
        await using var output = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await using (var writer = new TarWriter(output, leaveOpen: true))
        {
            await WriteEntryAsync(writer, "artifact.json", manifest, false);
            await WriteEntryAsync(writer, "payload/run-output", Encoding.UTF8.GetBytes("#!/bin/sh\nfind \"$(dirname \"$0\")/output\" -maxdepth 2 -type f -print\n"), true);
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(outputRoot, file).Replace('\\', '/');
                ValidateRelative(relative);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var entry = Entry("payload/output/" + relative, input, false);
                await writer.WriteEntryAsync(entry);
            }
        }
        if (new FileInfo(bundlePath).Length > maximumBytes)
            throw new InvalidDataException("The normalized toolchain bundle exceeds its approved limit.");
    }

    private static void CopyFixture(string source, string destination, long maximumBytes)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The certified fixture is missing from the immutable adapter package.");
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) throw new InvalidDataException("The certified fixture contains a symbolic link.");
            total = checked(total + info.Length);
            if (total > maximumBytes) throw new InvalidDataException("The certified fixture exceeds its source limit.");
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            ValidateRelative(relative);
            var target = ResolveBeneath(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static async Task UploadWhenReadyAsync(
        Process adapter,
        ToolchainBrokerClient broker,
        string outputRoot,
        long maximumOutputBytes)
    {
        var ready = Path.Combine(outputRoot, ".csweet-adapter-complete");
        while (!File.Exists(ready))
        {
            if (adapter.HasExited)
                throw new InvalidDataException("The toolchain adapter exited before requesting bounded output ingestion.");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        var bundle = Path.Combine(Path.GetDirectoryName(outputRoot)!, "toolchain-output.csab");
        try
        {
            await CreateOutputBundleAsync(outputRoot, bundle, maximumOutputBytes);
            await broker.UploadArtifactAsync(bundle);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, ".csweet-output-uploaded"), "ingested");
        }
        finally
        {
            if (File.Exists(bundle)) File.Delete(bundle);
        }
    }

    private static async Task WriteEntryAsync(TarWriter writer, string name, byte[] bytes, bool executable)
    {
        var stream = new MemoryStream(bytes, writable: false);
        await writer.WriteEntryAsync(Entry(name, stream, executable));
    }

    private static PaxTarEntry Entry(string name, Stream content, bool executable) => new(TarEntryType.RegularFile, name)
    {
        DataStream = content,
        Uid = 0,
        Gid = 0,
        UserName = string.Empty,
        GroupName = string.Empty,
        ModificationTime = NormalizedTime,
        Mode = executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
    };

    private static async Task CopyOutputAsync(StreamReader input, TextWriter output)
    {
        while (await input.ReadLineAsync() is { } line) await output.WriteLineAsync(line);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"Managed toolchain setting {name} is unavailable.");
    private static string RequiredPath(string name)
    {
        var path = Path.GetFullPath(Required(name));
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) == path)
            throw new InvalidDataException($"Managed toolchain path {name} is invalid.");
        return Path.TrimEndingDirectorySeparator(path);
    }
    private static long RequiredLong(string name, long minimum, long maximum) =>
        long.TryParse(Required(name), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
            ? value : throw new InvalidDataException($"Managed toolchain limit {name} is invalid.");
    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || Path.IsPathRooted(relative) ||
            relative.Split('/', StringSplitOptions.None).Any(s => s is "" or "." or "..") || relative.Any(char.IsControl))
            throw new InvalidDataException("A toolchain path is invalid.");
    }
    private static string ResolveBeneath(string root, string relative)
    {
        var value = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!value.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("A toolchain path escaped its disposable workspace.");
        return value;
    }
    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
    }
    private static string Sanitize(string value) => new(value.Where(c => !char.IsControl(c) || c == ' ').Take(1000).ToArray());
}

internal sealed class ToolchainBrokerClient : IAsyncDisposable
{
    private const int ChunkBytes = 768 * 1024;
    private readonly HttpClient http;

    public ToolchainBrokerClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> DownloadAsync(Uri uri, string destination, long maximumBytes)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        long offset = 0;
        string? archiveLayout = null;
        while (true)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { url = uri.AbsoluteUri, offset, maximumBytes = ChunkBytes });
            using var response = await http.PostAsync("/build/fetch", new ByteArrayContent(body));
            response.EnsureSuccessStatusCode();
            var responseLayout = Header(response, "X-CSweet-Archive-Layout") ?? "provider-rooted";
            if (archiveLayout is not null && !string.Equals(archiveLayout, responseLayout, StringComparison.Ordinal))
                throw new InvalidDataException("The source broker changed archive layout during transfer.");
            archiveLayout = responseLayout;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0 && !IsComplete(response)) throw new IOException("The source broker returned an empty incomplete chunk.");
            offset = checked(offset + bytes.Length);
            if (offset > maximumBytes) throw new InvalidDataException("The source archive exceeds its approved limit.");
            await output.WriteAsync(bytes);
            if (IsComplete(response)) { await output.FlushAsync(); return archiveLayout; }
        }
    }

    public async Task UploadArtifactAsync(string path)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous);
        var digest = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
        input.Position = 0;
        var buffer = new byte[ChunkBytes];
        long sequence = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer);
            if (read == 0) break;
            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.Add("X-CSweet-Sequence", sequence++.ToString(System.Globalization.CultureInfo.InvariantCulture));
            content.Headers.Add("X-CSweet-Completed", "false");
            using var response = await http.PostAsync("/build/artifact", content);
            response.EnsureSuccessStatusCode();
        }
        using var final = new ByteArrayContent([]);
        final.Headers.Add("X-CSweet-Sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        final.Headers.Add("X-CSweet-Completed", "true");
        final.Headers.Add("X-CSweet-Digest", digest);
        using var completed = await http.PostAsync("/build/artifact", final);
        completed.EnsureSuccessStatusCode();
    }

    private static bool IsComplete(HttpResponseMessage response) => response.Headers.TryGetValues("X-CSweet-Complete", out var values) &&
        string.Equals(values.SingleOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    public ValueTask DisposeAsync() { http.Dispose(); return ValueTask.CompletedTask; }
}
