namespace CSweet.Office.Tests;

public sealed class LinuxInstallationTests
{
    [Fact]
    public void Installer_IsBoundToSupportedUbuntuAndHardwareVirtualization()
    {
        var script = Read("scripts", "linux", "install-office.sh");

        Assert.Contains("Ubuntu 24.04 LTS", script, StringComparison.Ordinal);
        Assert.Contains("/sys/fs/cgroup/cgroup.controllers", script, StringComparison.Ordinal);
        Assert.Contains("/dev/kvm", script, StringComparison.Ordinal);
        Assert.Contains("group/world-writable", script, StringComparison.Ordinal);
        Assert.Contains("Execution packages may not contain symbolic links", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Configurator_ExplainsInformationalSecurityLabelsAndKeepsTokenOffCommandLine()
    {
        var script = Read("scripts", "linux", "configure-office.sh");

        Assert.Contains("Security label: Baseline", script, StringComparison.Ordinal);
        Assert.Contains("This label is informational and does not disable agents", script, StringComparison.Ordinal);
        Assert.Contains("--dedicated-host", script, StringComparison.Ordinal);
        Assert.Contains("--accept-baseline-risk", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--enrollment-token", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePackage_InstallsTheManualConfigurator()
    {
        var script = Read("scripts", "linux", "new-native-packages.sh");

        Assert.Contains("/usr/sbin/csweet-configure-office", script, StringComparison.Ordinal);
        Assert.Contains("csweet-office_", script, StringComparison.Ordinal);
        Assert.Contains("deb_arch=amd64", script, StringComparison.Ordinal);
        Assert.Contains("deb_arch=arm64", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRelease_PublishesCertifiedUbuntuDebOnly()
    {
        var release = Read("scripts", "release", "Invoke-LinuxRelease.ps1");
        var manifest = Read("scripts", "release", "New-OfficeReleaseManifest.ps1");

        Assert.Contains("--format deb", release, StringComparison.Ordinal);
        Assert.DoesNotContain("--format all", release, StringComparison.Ordinal);
        Assert.DoesNotContain("Pattern = '*.rpm'", manifest, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CSweet.Office.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
