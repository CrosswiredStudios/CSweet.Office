using CSweet.Office.Runtime.Abstractions;

namespace CSweet.Office.Runtime.HyperV;

public sealed class HyperVSocketTransportOptions : CSweet.Isolation.HyperV.HyperVSocketTransportOptions;
public static class WindowsHyperVSocketServiceRegistration
{
    public const string RegistryBasePath = CSweet.Isolation.HyperV.WindowsHyperVSocketServiceRegistration.RegistryBasePath;
    public static string ServiceKeyPath(Guid serviceId) => CSweet.Isolation.HyperV.WindowsHyperVSocketServiceRegistration.ServiceKeyPath(serviceId);
    public static string LegacyBracedServiceKeyPath(Guid serviceId) => CSweet.Isolation.HyperV.WindowsHyperVSocketServiceRegistration.LegacyBracedServiceKeyPath(serviceId);
}
public interface IHyperVGuestTransport
{
    Task<Stream> ConnectAsync(Guid virtualMachineId, CancellationToken cancellationToken = default);
}
public sealed class WindowsHyperVSocketTransport(HyperVSocketTransportOptions options) : IHyperVGuestTransport, IPlatformGuestChannelConnector
{
    private readonly CSweet.Isolation.HyperV.WindowsHyperVSocketTransport inner = new(options);
    public string ProviderId => IsolationProviderCatalog.HyperV().ProviderId;
    public Task<Stream> ConnectAsync(Guid virtualMachineId, CancellationToken cancellationToken = default) =>
        inner.ConnectAsync(virtualMachineId, cancellationToken);
    public Task<Stream> OpenGuestChannelAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
    {
        if (handle.ProviderId != ProviderId || !Guid.TryParseExact(handle.ProviderInstanceId, "N", out var id))
            throw new InvalidDataException("The Hyper-V workload handle is invalid.");
        return inner.ConnectAsync(id, cancellationToken);
    }
}
