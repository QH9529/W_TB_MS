using Xunit;

namespace W_TB_MS.Tests;

public class ParameterWriteTests
{
    [Fact]
    public void EncodeTemperature_UsesTenthsScale()
    {
        bool success = MainForm.TryEncodeParameterValue(40204, "12.3", out ushort rawValue, out string error);

        Assert.True(success, error);
        Assert.Equal((ushort)123, rawValue);
    }

    [Fact]
    public void EncodeNegativeCompensation_UsesSignedRegisterValue()
    {
        bool success = MainForm.TryEncodeParameterValue(40301, "-5.0", out ushort rawValue, out string error);

        Assert.True(success, error);
        Assert.Equal(unchecked((ushort)(short)-50), rawValue);
    }

    [Fact]
    public void EncodeOutOfRangeValue_IsRejected()
    {
        bool success = MainForm.TryEncodeParameterValue(40001, "2", out _, out string error);

        Assert.False(success);
        Assert.Contains("超出", error);
    }

    [Fact]
    public void EncodePermanentDuration_AcceptsFFFF()
    {
        bool success = MainForm.TryEncodeParameterValue(40004, "0xFFFF", out ushort rawValue, out string error);

        Assert.True(success, error);
        Assert.Equal(ushort.MaxValue, rawValue);
    }

    [Fact]
    public void EncodeHotWaterOutletTemp_UsesHundredthsScale()
    {
        // 40701：传输值 = 实际值 × 100
        bool success = MainForm.TryEncodeParameterValue(40701, "65.5", out ushort rawValue, out string error);

        Assert.True(success, error);
        Assert.Equal((ushort)6550, rawValue);
    }

    [Fact]
    public void EncodeHotWaterOutletTemp_RejectsMoreThanTwoDecimals()
    {
        bool success = MainForm.TryEncodeParameterValue(40701, "65.555", out _, out string error);

        Assert.False(success);
        Assert.Contains("2位小数", error);
    }

    [Fact]
    public void BuildUnix64Words_SplitsLowWordFirst()
    {
        bool success = MainForm.TryBuildUnix64Words("1697558400", out ushort[] words, out string error);

        Assert.True(success, error);
        Assert.Equal(new ushort[] { 0xAF80, 0x652E, 0x0000, 0x0000 }, words);
    }

    [Fact]
    public void BuildUnix64Words_RejectsGarbageInput()
    {
        bool success = MainForm.TryBuildUnix64Words("昨天下午", out _, out string error);

        Assert.False(success);
        Assert.Contains("无法识别", error);
    }

    [Fact]
    public void DescribeWriteValueMeaning_ShowsSelectedAndAllOptions()
    {
        // 40201 工作模式：1=制冷、2=制热（用户要求写入确认弹窗里带"1对应XX"的描述）
        string text = MainForm.DescribeWriteValueMeaning(40201, 2);

        Assert.Contains("本次 2 = 制热", text);
        Assert.Contains("可选值：1 = 制冷 / 2 = 制热", text);
    }

    [Fact]
    public void DescribeWriteValueMeaning_FallsBackToProtocolCommandMap()
    {
        // 40106 清除设备信息不是参数表枚举，取协议里的 ClearDeviceCmdMap
        string text = MainForm.DescribeWriteValueMeaning(40106, 1);

        Assert.Contains("本次 1 = 服务密码参数恢复出厂设置", text);
        Assert.Contains("0 = 所有参数恢复出厂设置", text);
    }

    [Fact]
    public void DescribeWriteValueMeaning_FlagsUndefinedOption()
    {
        string text = MainForm.DescribeWriteValueMeaning(40002, 9);

        Assert.Contains("协议未定义该取值", text);
    }

    [Fact]
    public void DescribeWriteValueMeaning_IsEmptyForContinuousParameters()
    {
        // 40204 制冷进水温度是连续量，没有枚举含义
        Assert.Equal("", MainForm.DescribeWriteValueMeaning(40204, 120));
    }

    [Fact]
    public void FormatValueMeaningList_ReturnsEmptyForNonEnumeratedRegister()
    {
        Assert.Equal("", MainForm.FormatValueMeaningList(40306));
    }

    [Fact]
    public void DescribeWriteValueMeaning_ClearFaultZeroMeansClearAll()
    {
        // 40101 手动解除故障报警：写 0 解除全部（协议：0=解除所有故障报警）
        string text = MainForm.DescribeWriteValueMeaning(40101, 0);

        Assert.Contains("本次 0 = 解除所有故障报警", text);
        Assert.Contains("0 = 解除所有故障报警", text);
    }

    [Fact]
    public void DescribeWriteValueMeaning_ClearFaultResolvesFaultCodeName()
    {
        // 写 3 应对应到协议附录故障码表里的「热水流量不足」
        string text = MainForm.DescribeWriteValueMeaning(40101, 3);

        Assert.Contains("本次 3 = 解除 故障码 3（热水流量不足） 的报警", text);
    }

    [Fact]
    public void DescribeWriteValueMeaning_ClearFaultFlagsUnknownCode()
    {
        // 65 以上（除 100 外）协议故障码表里没有
        string text = MainForm.DescribeWriteValueMeaning(40101, 90);

        Assert.Contains("协议附录未定义该故障码", text);
    }

    [Fact]
    public void FormatValueMeaningList_ClearFaultShowsRangeSummary()
    {
        string text = MainForm.FormatValueMeaningList(40101);

        Assert.Contains("0 = 解除所有故障报警", text);
        Assert.Contains("解除对应故障码报警", text);
    }

    [Fact]
    public void ValueMeanings_MatchProtocolWordingForCorrectedRegisters()
    {
        // 这几处旧文案与协议语义不符，已按协议更正
        Assert.Contains("禁止除霜", MainForm.FormatValueMeaningList(40103));
        Assert.Contains("允许除霜", MainForm.FormatValueMeaningList(40103));
        Assert.Contains("未超过2小时", MainForm.FormatValueMeaningList(40105));
        Assert.Contains("已超过2小时", MainForm.FormatValueMeaningList(40105));
        Assert.Contains("持续运行", MainForm.FormatValueMeaningList(40223));
    }
}
