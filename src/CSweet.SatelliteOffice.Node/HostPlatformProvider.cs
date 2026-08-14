using CSweet.SatelliteOffice.Runtime.Abstractions;

namespace CSweet.SatelliteOffice.Node;

internal static class HostPlatformProvider
{
    public static IsolationProviderDescriptor Resolve() => OperatingSystem.IsWindows()
        ? IsolationProviderCatalog.HyperV()
        : OperatingSystem.IsLinux()
            ? IsolationProviderCatalog.Firecracker()
            : OperatingSystem.IsMacOS()
                ? IsolationProviderCatalog.AppleVirtualization()
                : throw new PlatformNotSupportedException(
                    "C-Sweet Satellite Office does not support this host operating system.");
}
