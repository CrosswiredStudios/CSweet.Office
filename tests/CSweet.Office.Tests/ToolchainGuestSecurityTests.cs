using System.IO.Compression;

namespace CSweet.Office.Tests;

public sealed class ToolchainGuestSecurityTests
{
    [Fact]
    public void OfflineSourceUsesOnlyExactBrokerBuildReference()
    {
        var build = Guid.NewGuid(); var commit = new string('a', 40);
        var source = $"csweet-source://build/{build:N}/{commit}";
        Assert.Equal(source, ToolchainGuestProgram.SourceArchiveUri(new Uri("http://localhost/internal.git"), commit, source, build.ToString()).AbsoluteUri);
        foreach (var invalid in new[] { source + "?redirect=1", source + "#fragment", source.Replace(commit, new string('b', 40)),
            source.Replace(build.ToString("N"), Guid.NewGuid().ToString("N")), "https://example.com/source.zip" })
            Assert.Throws<InvalidDataException>(() => ToolchainGuestProgram.SourceArchiveUri(new Uri("http://localhost/internal.git"), commit, invalid, build.ToString()));
    }

    [Fact]
    public void ExistingGitHubSourceRemainsAnExactArchive()
    {
        var commit = new string('a', 40);
        Assert.Equal($"https://codeload.github.com/owner/repo/zip/{commit}",
            ToolchainGuestProgram.SourceArchiveUri(new Uri("https://github.com/owner/repo.git"), commit).AbsoluteUri);
    }

    [Fact]
    public void SourceExtractionRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-toolchain-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("repo/../escape.txt").Open());
                writer.Write("escape");
            }

            Assert.Throws<InvalidDataException>(() => ToolchainGuestProgram.ExtractSourceArchive(
                archivePath, Path.Combine(root, "source"), 1024));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SourceExtractionRejectsExpandedByteOverflow()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-toolchain-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using var content = archive.CreateEntry("repo/large.bin").Open();
                content.Write(new byte[2048]);
            }

            Assert.Throws<InvalidDataException>(() => ToolchainGuestProgram.ExtractSourceArchive(
                archivePath, Path.Combine(root, "source"), 1024));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrustedFlatSourceArchivePreservesRepositoryRootFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "csweet-toolchain-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archivePath = Path.Combine(root, "source.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("package.json").Open()))
                    writer.Write("{}");
                using (var writer = new StreamWriter(archive.CreateEntry("src/game.ts").Open()))
                    writer.Write("export {};");
            }
            var destination = Path.Combine(root, "source");

            ToolchainGuestProgram.ExtractSourceArchive(
                archivePath, destination, 1024, flatLayout: true);

            Assert.True(File.Exists(Path.Combine(destination, "package.json")));
            Assert.True(File.Exists(Path.Combine(destination, "src", "game.ts")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
