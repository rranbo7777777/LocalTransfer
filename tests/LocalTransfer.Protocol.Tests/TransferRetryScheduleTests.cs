using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Protocol.Tests;

/// <summary>
/// The retry schedule decides whether a brief network drop aborts a transfer or merely pauses it,
/// so its shape is pinned here instead of being left to the constants alone.
/// </summary>
public sealed class TransferRetryScheduleTests
{
    [Fact]
    public void RetryDelay_GrowsExponentially()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), TransferTuning.GetTransferRetryDelay(1));
        Assert.Equal(TimeSpan.FromMilliseconds(750), TransferTuning.GetTransferRetryDelay(2));
        Assert.Equal(TimeSpan.FromMilliseconds(2250), TransferTuning.GetTransferRetryDelay(3));
    }

    [Fact]
    public void RetryDelay_IsCappedAtTheMaximum()
    {
        Assert.Equal(TransferTuning.TransferRetryMaxDelay, TransferTuning.GetTransferRetryDelay(10));
        Assert.Equal(TransferTuning.TransferRetryMaxDelay, TransferTuning.GetTransferRetryDelay(100));
    }

    [Fact]
    public void RetryDelay_RejectsNonPositiveAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferTuning.GetTransferRetryDelay(0));
    }

    [Fact]
    public void TotalRetryDelay_SumsEveryAttemptBeforeTheLast()
    {
        var expected = TimeSpan.Zero;
        for (var attempt = 1; attempt < TransferTuning.TransferMaxAttempts; attempt++)
        {
            expected += TransferTuning.GetTransferRetryDelay(attempt);
        }

        Assert.Equal(expected, TransferTuning.TotalTransferRetryDelay);
    }

    [Fact]
    public void RecoveryBudget_OutlastsSeveralRequestFailureCycles()
    {
        // If one exhausting request could burn the whole recovery budget, the outer resume loop
        // would never get a chance to run - which is exactly the failure mode it exists to fix.
        var cycles = TransferTuning.TransferRecoveryBudget / TransferTuning.TotalTransferRetryDelay;
        Assert.True(cycles >= 10, $"The recovery budget fits only {cycles} request failure cycles.");
    }
}
