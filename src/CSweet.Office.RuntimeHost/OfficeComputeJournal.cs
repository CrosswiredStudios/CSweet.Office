using System.Security.Principal;
using CSweet.Compute.Runtime;

namespace CSweet.Office.RuntimeHost;

internal static class OfficeComputeJournal
{
    public static ComputeReplayJournal Open(string directory, ComputeProviderEnrollment enrollment,
        ComputeProviderCapacity capacity, TimeProvider clock)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var owner = (SecurityIdentifier)new NTAccount(@"NT SERVICE\CSweet.Office.RuntimeHost").Translate(typeof(SecurityIdentifier));
        return new ComputeReplayJournal(directory, enrollment, capacity, clock, path => WindowsComputeProtectedPaths.Verify(path, owner));
    }
}
