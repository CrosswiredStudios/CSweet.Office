using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.RuntimeHost;

internal static class HostPlatformProvider
{
    public static IsolationProviderDescriptor Resolve() => OperatingSystem.IsWindows()
        ? IsolationProviderCatalog.HyperV()
        : OperatingSystem.IsLinux()
            ? IsolationProviderCatalog.Firecracker()
            : OperatingSystem.IsMacOS()
                ? IsolationProviderCatalog.AppleVirtualization()
                : throw new PlatformNotSupportedException(
                    "C-Sweet Office does not support this host operating system.");
}
