using Xunit;

namespace W_TB_MS.Tests;

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
    [InlineData(30242, 253, 25.3)]
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

    [Theory]
    [InlineData(0, "0 min 00 s")]
    [InlineData(59, "0 min 59 s")]
    [InlineData(60, "1 min 00 s")]
    [InlineData(3661, "61 min 01 s")]
    public void FormatDurationSeconds_UsesMinutesAndPaddedSeconds(double seconds, string expected)
    {
        Assert.Equal(expected, MainForm.FormatDurationSeconds(seconds));
    }

    [Fact]
    public void FormatDurationSeconds_ClampsInvalidNegativeValue()
    {
        Assert.Equal("0 min 00 s", MainForm.FormatDurationSeconds(-1));
        Assert.Equal("-- min -- s", MainForm.FormatDurationSeconds(double.NaN));
    }

    [Theory]
    [InlineData(40001, 0x0001, "0x0001 / 开机")]
    [InlineData(40002, 0x0002, "0x0002 / 超级静音")]
    [InlineData(40201, 0x0002, "0x0002 / 制热")]
    [InlineData(40228, 0x0002, "0x0002 / 自动")]
    public void FormatParameterValue_IncludesDefinedStateMeaning(
        ushort address,
        ushort rawValue,
        string expected)
    {
        Assert.Equal(expected, MainForm.FormatParameterValue(address, rawValue));
    }

    [Fact]
    public void FormatParameterValue_LeavesNumericParametersScaled()
    {
        Assert.Equal("23.5", MainForm.FormatParameterValue(40204, 235));
    }

    [Fact]
    public void FormatParameterValue_KeepsTwoDecimalsForHundredthsScale()
    {
        // 40701 热水器出水温度：传输值 = 实际值 × 100，6500 应为 65.00℃
        Assert.Equal("65.00", MainForm.FormatParameterValue(40701, 6500));
    }

    [Theory]
    [InlineData(20060, 1, "1 / 打开")]                                  // 风盘1 阀开关
    [InlineData(20073, 403, "40.3")]                                    // 风盘1 温度：传输值 = 实际值 × 10
    [InlineData(20079, 0x00AC, "172 / 自研交流风盘型号")]                // 风盘1 风盘型号
    [InlineData(20071, 0, "0")]                                         // 风盘1 CO2 浓度
    public void FormatParameterValue_DecodesFan4GTemplate(ushort address, ushort rawValue, string expected)
    {
        Assert.Equal(expected, MainForm.FormatParameterValue(address, rawValue));
    }

    [Fact]
    public void FormatOnlineBitmap_ListsOnlineDevicesFromBits1To15()
    {
        Assert.Equal("0x0000 / 在线风盘：无", MainForm.FormatOnlineBitmap(0x0000, "风盘"));
        Assert.Equal("0x0001 / 在线风盘：无", MainForm.FormatOnlineBitmap(0x0001, "风盘")); // BIT0 保留
        Assert.Equal("0x0006 / 在线风盘：1、2", MainForm.FormatOnlineBitmap(0x0006, "风盘")); // BIT1=1号, BIT2=2号
        Assert.Equal("0x8000 / 在线分集水器：15", MainForm.FormatOnlineBitmap(0x8000, "分集水器"));
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
        Assert.Equal(13, MainForm.StoredBitRegisterCount);
        Assert.Equal(117, MainForm.StoredBitCurveCount);

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
