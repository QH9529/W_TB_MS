using Xunit;

namespace W_TB_jiankong.Tests;

public class ChartHoverTests
{
    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1.6, 1)]
    [InlineData(2.8, 2)]
    [InlineData(4.0, 3)]
    public void FindNearestTimeIndex_ReturnsClosestSample(double target, int expectedIndex)
    {
        double[] times = { 1.0, 2.0, 3.0, 4.0 };

        Assert.Equal(expectedIndex, MainForm.FindNearestTimeIndex(times, target));
    }

    [Fact]
    public void FindNearestTimeIndex_ReturnsMinusOneForEmptyData()
    {
        Assert.Equal(-1, MainForm.FindNearestTimeIndex(Array.Empty<double>(), 1.0));
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(2.0, 1)]
    [InlineData(3.0, 3)]
    [InlineData(5.0, 4)]
    public void FindFirstTimeIndexAtOrAfter_ReturnsInsertionIndex(double target, int expectedIndex)
    {
        double[] times = { 1.0, 2.0, 2.0, 4.0 };

        Assert.Equal(expectedIndex, MainForm.FindFirstTimeIndexAtOrAfter(times, target));
    }

    [Fact]
    public void CurveRetention_IsTwentyFourHours()
    {
        Assert.Equal(TimeSpan.FromHours(24), MainForm.CurveRetentionDuration);
    }

    [Fact]
    public void AutomaticArchiveInterval_IsSixHours()
    {
        Assert.Equal(TimeSpan.FromHours(6), MainForm.AutomaticArchiveIntervalDuration);
    }

    [Theory]
    [InlineData(30108, 65413, -12.3)]
    [InlineData(30215, 456, 45.6)]
    [InlineData(30232, 87, 8.7)]
    [InlineData(30233, 9500, 9500)]
    [InlineData(30237, 10000, 10000)]
    public void DecodeNumericCurveValue_MatchesDisplayedDecimalValue(
        ushort address,
        ushort rawValue,
        double expected)
    {
        var values = new Dictionary<ushort, ushort> { [address] = rawValue };

        Assert.Equal(expected, MainForm.DecodeNumericCurveValue(address, values), 6);
    }

    [Fact]
    public void DecodeNumericCurveValue_CombinesWordsBeforeDecimalScaling()
    {
        var values = new Dictionary<ushort, ushort>
        {
            [30217] = 0x0001,
            [30218] = 0x86A0,
            [30235] = 0x2710,
            [30236] = 0x0000
        };

        Assert.Equal(10000.0, MainForm.DecodeNumericCurveValue(30217, values), 6);
        Assert.Equal(10000.0, MainForm.DecodeNumericCurveValue(30235, values), 6);
    }

    [Fact]
    public void CanRecordCurveSample_RequiresEveryCurrentRegister()
    {
        var values = MainForm.RequiredCurveSampleAddresses.ToDictionary(address => address, _ => (ushort)0);

        Assert.True(MainForm.CanRecordCurveSample(values));

        values.Remove(MainForm.RequiredCurveSampleAddresses.First());
        Assert.False(MainForm.CanRecordCurveSample(values));
    }

    [Fact]
    public void DecodeBitValues_ExpandsOnlyRequestedBit()
    {
        ushort[] rawValues = { 0b0000, 0b0010, 0b0011, 0b0001 };

        Assert.Equal(new[] { 0.0, 1.0, 1.0, 0.0 }, MainForm.DecodeBitValues(rawValues, 1));
        Assert.Equal(new[] { 1.0, 1.0 }, MainForm.DecodeBitValues(rawValues, 0, 2, 2));
    }

    [Fact]
    public void FullBitRegisterStorage_PreservesEveryDefinedBitCurve()
    {
        Assert.Equal(8, MainForm.StoredBitRegisterCount);
        Assert.Equal(110, MainForm.StoredBitCurveCount);

        ushort[] samples = { 0x0000, 0xFFFF, 0xA55A };
        for (int bit = 0; bit < 16; bit++)
        {
            List<double> decoded = MainForm.DecodeBitValues(samples, bit);
            Assert.Equal(0.0, decoded[0]);
            Assert.Equal(1.0, decoded[1]);
            Assert.Equal((samples[2] & (1 << bit)) == 0 ? 0.0 : 1.0, decoded[2]);
        }
    }
}
