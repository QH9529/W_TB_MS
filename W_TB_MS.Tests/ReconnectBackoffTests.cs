using W_TB_MS;
using Xunit;

namespace W_TB_MS.Tests;

public class ReconnectBackoffTests
{
    [Theory]
    [InlineData(1, 1000)]   // 第1次: 1s
    [InlineData(2, 2000)]   // 第2次: 2s
    [InlineData(3, 4000)]   // 第3次: 4s
    [InlineData(4, 8000)]   // 第4次: 8s
    [InlineData(5, 16000)]  // 第5次: 16s
    [InlineData(6, 30000)]  // 第6次: 32s → 封顶30s
    [InlineData(7, 30000)]  // 第7次: 64s → 封顶30s
    public void ComputeReconnectDelayMs_ExponentialGrowth_CappedAt30s(int attempt, int expectedMs)
    {
        Assert.Equal(expectedMs, MainForm.ComputeReconnectDelayMs(attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ComputeReconnectDelayMs_NonPositiveAttempt_ReturnsBaseDelay(int attempt)
    {
        Assert.Equal(1000, MainForm.ComputeReconnectDelayMs(attempt));
    }
}
