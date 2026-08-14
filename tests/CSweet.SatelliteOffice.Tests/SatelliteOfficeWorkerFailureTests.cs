using CSweet.SatelliteOffice.Node;
using CSweet.SatelliteOffice.Runtime.Abstractions;
using Grpc.Core;

namespace CSweet.SatelliteOffice.Tests;

public sealed class SatelliteOfficeWorkerFailureTests
{
    [Fact]
    public void PreservesBoundedActionableHeadquartersBrokerFailure()
    {
        var failure = SatelliteOfficeWorker.DescribeExecutionFailure(
            new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "The authenticated guest broker protocol was rejected.\0")));

        Assert.Equal("headquarters-broker-rejected", failure.FailureCode);
        Assert.Contains("guest broker protocol", failure.SanitizedFailure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\0', failure.SanitizedFailure);
    }

    [Fact]
    public void DoesNotExposeUnexpectedExceptionMessage()
    {
        var failure = SatelliteOfficeWorker.DescribeExecutionFailure(
            new InvalidOperationException("password=do-not-leak"));

        Assert.Equal("satellite-office-error", failure.FailureCode);
        Assert.DoesNotContain("do-not-leak", failure.SanitizedFailure, StringComparison.Ordinal);
        Assert.Contains("assignment identifier", failure.SanitizedFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsIsolationFailureActionable()
    {
        var failure = SatelliteOfficeWorker.DescribeExecutionFailure(
            new IsolationUnavailableException("Hyper-V is not enabled."));

        Assert.Equal("isolation-provider-unavailable", failure.FailureCode);
        Assert.Equal("Hyper-V is not enabled.", failure.SanitizedFailure);
    }
}
