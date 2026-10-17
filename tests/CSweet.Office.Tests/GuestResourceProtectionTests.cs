using CSweet.Office.RuntimeGuest;

namespace CSweet.Office.Tests;

public sealed class GuestResourceProtectionTests
{
    [Theory]
    [InlineData(1024, 1024, 768)]
    [InlineData(4096, 4096, 3072)]
    [InlineData(8192, 4096, 3072)]
    public void Guest_reserves_supervision_capacity_and_never_exceeds_authorized_memory(long physical, long authorized, long expected)
        => Assert.Equal(expected * 1024 * 1024, GuestWorkloadSupervisor.WorkloadMemoryBudget(physical * 1024 * 1024, authorized * 1024 * 1024));

    [Fact]
    public void Insufficient_guest_capacity_fails_before_launch() => Assert.Throws<InvalidDataException>(() =>
        GuestWorkloadSupervisor.WorkloadMemoryBudget(384 * 1024 * 1024, 1024 * 1024 * 1024));
}
