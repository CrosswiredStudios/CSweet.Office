using CSweet.Office.Runtime.Protocol;

namespace CSweet.Office.Runtime.LocalRpc;

public interface IOfficeComputePartition
{
    Task<ComputeExchangeResult> ExchangeAsync(ComputeExchange request, CancellationToken token);
}
