using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using CSweet.Compute.Contracts;
using CSweet.Compute.Runtime;
using CSweet.Office.Contracts.ControlPlane;
using CSweet.Office.Runtime.LocalRpc;
using CSweet.Office.Runtime.Protocol;
using Google.Protobuf;

namespace CSweet.Office.Node;

public sealed partial class OfficeWorker
{
    private readonly Channel<ComputeExchange> computeInbox = Channel.CreateBounded<ComputeExchange>(
        new BoundedChannelOptions(8) { SingleReader = true });
    private sealed record ComputeInventory(OfficeComputeOffer? Offer, ByteString? Result, ComputeMaintenanceOutboxEntry? Maintenance);
    private ComputeInventory? computeInventory;
    private string lastSentComputeOperation = "";

    private async Task RelayComputeAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var client = _providers.Values.OfType<RuntimeHostProviderClient>().SingleOrDefault(x =>
            x.Descriptor.ProviderId == (OperatingSystem.IsMacOS() ? "apple-virtualization" : "hyperv-gen2"));
        if (client is null) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (computeInbox.Reader.TryRead(out var request)) await client.ExchangeComputeAsync(request, token);
                var inventory = await client.ExchangeComputeAsync(new() { Inventory = true,
                    AfterOperationId = Volatile.Read(ref lastSentComputeOperation) }, token);
                Volatile.Write(ref computeInventory, new(inventory.CapacityOffer.IsEmpty ? null :
                    OfficeComputeOffer.Parser.ParseFrom(inventory.CapacityOffer), inventory.ResultPayloads.FirstOrDefault(),
                    inventory.MaintenanceEntry.IsEmpty ? null : JsonSerializer.Deserialize<ComputeMaintenanceOutboxEntry>(
                        inventory.MaintenanceEntry.Span, ComputeProtocol.Json)));
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                Volatile.Write(ref computeInventory, null);
                logger.LogWarning(e, "The installed Office compute partition is unavailable.");
            }
            using var interval = CancellationTokenSource.CreateLinkedTokenSource(token);
            interval.CancelAfter(TimeSpan.FromSeconds(10));
            try { await computeInbox.Reader.WaitToReadAsync(interval.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    private static OfficeControlMessage SignComputeMaintenance(OfficeState state, X509Certificate2 cert, ComputeMaintenanceOutboxEntry entry)
    {
        using var key = cert.GetECDsaPublicKey() ?? throw new CryptographicException("Unavailable Office identity.");
        var id = "office-" + Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..40];
        var signed = new ComputeMaintenanceSigner(cert, id, TimeProvider.System).Sign(entry);
        return new() { ProtocolVersion = "1.0", OfficeId = state.OfficeId.ToString("D"), SessionEpoch = state.SessionEpoch,
            ComputeMaintenance = new() { SignedDeliveryJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(signed, ComputeProtocol.Json)) } };
    }

    private static OfficeControlMessage SignComputeResult(OfficeState state, X509Certificate2 certificate, ByteString payload, out string operationId)
    {
        var result = JsonSerializer.Deserialize<ComputeProviderResult>(payload.Span, ComputeProtocol.Json);
        if (result?.NodeId != state.OfficeId || result.ProviderId != (OperatingSystem.IsMacOS() ? "office-apple-virtualization" : "office-hyperv"))
            throw new InvalidDataException("The compute result belongs to another Office.");
        operationId = result.OperationId.ToString("D");
        using var key = certificate.GetECDsaPrivateKey() ?? throw new CryptographicException("Unavailable Office identity.");
        var id = "office-" + Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..40];
        var json = payload.ToStringUtf8();
        var signed = new SignedComputeProviderResult(id, json,
            Convert.ToBase64String(key.SignData(ComputeProtocol.ResultPayload(json), HashAlgorithmName.SHA256)));
        return new() { ProtocolVersion = "1.0", OfficeId = state.OfficeId.ToString("D"), SessionEpoch = state.SessionEpoch,
            ComputeResult = new() { SignedResultJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(signed, ComputeProtocol.Json)) } };
    }
}
