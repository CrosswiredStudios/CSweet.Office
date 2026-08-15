using CSweet.Office.Node;
using CSweet.Office.Runtime.Abstractions;
using Grpc.Core;

namespace CSweet.Office.Tests;

public sealed class OfficeWorkerFailureTests
{
    [Fact]
    public void PreservesBoundedActionableHeadquartersBrokerFailure()
    {
        var failure = OfficeWorker.DescribeExecutionFailure(
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
        var failure = OfficeWorker.DescribeExecutionFailure(
            new InvalidOperationException("password=do-not-leak"));

        Assert.Equal("office-error", failure.FailureCode);
        Assert.DoesNotContain("do-not-leak", failure.SanitizedFailure, StringComparison.Ordinal);
        Assert.Contains("assignment identifier", failure.SanitizedFailure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsIsolationFailureActionable()
    {
        var failure = OfficeWorker.DescribeExecutionFailure(
            new IsolationUnavailableException("Hyper-V is not enabled."));

        Assert.Equal("isolation-provider-unavailable", failure.FailureCode);
        Assert.Equal("Hyper-V is not enabled.", failure.SanitizedFailure);
    }
}
