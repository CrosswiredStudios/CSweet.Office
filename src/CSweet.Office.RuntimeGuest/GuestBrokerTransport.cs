using System.ComponentModel;
using System.Runtime.InteropServices;
using CSweet.Office.Runtime.Protocol;
using Microsoft.Win32.SafeHandles;

namespace CSweet.Office.RuntimeGuest;

public sealed record GuestBrokerConnection(Stream Input, Stream Output) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        if (ReferenceEquals(Input, Output))
        {
            await Input.DisposeAsync();
            return;
        }
        await Input.DisposeAsync();
        await Output.DisposeAsync();
    }
}

public interface IGuestBrokerTransport
{
    Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default);
}

public sealed class StandardIoGuestBrokerTransport : IGuestBrokerTransport
{
    public Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GuestBrokerConnection(
            Console.OpenStandardInput(), Console.OpenStandardOutput()));
    }
}

public sealed class LinuxHyperVSocketGuestTransport(int port = 2761) : IGuestBrokerTransport
{
    private readonly CSweet.Isolation.HyperV.LinuxHyperVSocketGuestTransport transport = new(port);
    public async Task<GuestBrokerConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var result = await transport.AcceptAsync(cancellationToken);
        return new(result.Input, result.Output);
    }
    internal static GuestBrokerConnection OpenAcceptedConnection(SafeFileHandle inputHandle, SafeFileHandle outputHandle)
    {
        var result = CSweet.Isolation.HyperV.LinuxHyperVSocketGuestTransport.OpenAcceptedConnection(inputHandle, outputHandle);
        return new(result.Input, result.Output);
    }
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    internal struct LinuxSockAddrVm { public ushort Family; public ushort Reserved; public uint Port; public uint ContextId; }
}
public static class GuestBootConfigurationReader
{
    public static async Task<GuestBootConfiguration> ReadAsync(
        Stream input,
        int maximumFrameBytes = 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var envelope = await LengthDelimitedProtobuf.ReadAsync(
            input, GuestEnvelope.Parser, maximumFrameBytes, cancellationToken)
            ?? throw new EndOfStreamException("The host closed the guest bootstrap channel.");
        if (envelope.BodyCase != GuestEnvelope.BodyOneofCase.BootConfiguration ||
            !Guid.TryParseExact(envelope.MessageId, "N", out _))
            throw new InvalidDataException("The host did not send a valid guest boot configuration.");
        return envelope.BootConfiguration;
    }
}
