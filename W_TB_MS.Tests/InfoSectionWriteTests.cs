using W_TB_MS.Models;
using Xunit;

namespace W_TB_MS.Tests;

/// <summary>
/// 热泵主机 405xx 读写信息段的写入能力：
/// 40510 室外机激活走普通单寄存器枚举写入；40501 IMEI / 40511 激活 MD5 / 40519 单机并联模式
/// 是多寄存器数据块，必须整段 0x10 写入。另附 4G_OTA 与 Flash 段"界面只读"的分类校验。
/// </summary>
public class InfoSectionWriteTests
{
    [Fact]
    public void OutdoorActivate_IsWritableEnumWithin0To2()
    {
        Assert.True(MainForm.TryEncodeParameterValue(
            RegisterMap.OUTDOOR_ACTIVATE_ADDR, "1", out ushort rawValue, out string error), error);

        Assert.Equal((ushort)1, rawValue);
        Assert.Contains("允许使用", MainForm.FormatValueMeaningList(RegisterMap.OUTDOOR_ACTIVATE_ADDR));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("2", true)]
    [InlineData("3", false)]
    public void OutdoorActivate_RangeIsChecked(string input, bool expected)
    {
        bool success = MainForm.TryEncodeParameterValue(
            RegisterMap.OUTDOOR_ACTIVATE_ADDR, input, out _, out _);

        Assert.Equal(expected, success);
    }

    [Fact]
    public void BlockWriteDefinitions_CoverImeiMd5AndUnitMode()
    {
        Assert.True(MainForm.TryGetBlockWriteDefinition(RegisterMap.IMEI_ADDR, out var imei));
        Assert.Equal(40501, imei!.StartAddress);
        Assert.Equal(9, imei.RegisterCount);

        Assert.True(MainForm.TryGetBlockWriteDefinition(RegisterMap.ACTIVATION_MD5_START_ADDR, out var md5));
        Assert.Equal(40511, md5!.StartAddress);
        Assert.Equal(8, md5.RegisterCount);

        Assert.True(MainForm.TryGetBlockWriteDefinition(RegisterMap.UNIT_MODE_ADDR, out var mode));
        Assert.Equal(40519, mode!.StartAddress);
        Assert.Equal(9, mode.RegisterCount);

        // 室外机激活不是数据块，走 WriteRules 的单寄存器枚举路径
        Assert.False(MainForm.TryGetBlockWriteDefinition(RegisterMap.OUTDOOR_ACTIVATE_ADDR, out _));
    }

    [Fact]
    public void BlockWrites_DoNotOverlapEachOtherOrActivateRegister()
    {
        var ranges = new List<(int Start, int End)>();
        foreach (ushort address in new[]
                 {
                     RegisterMap.IMEI_ADDR,
                     RegisterMap.ACTIVATION_MD5_START_ADDR,
                     RegisterMap.UNIT_MODE_ADDR
                 })
        {
            Assert.True(MainForm.TryGetBlockWriteDefinition(address, out var definition));
            ranges.Add((definition!.StartAddress, definition.StartAddress + definition.RegisterCount - 1));
        }

        ranges.Sort();
        for (int i = 1; i < ranges.Count; i++)
            Assert.True(ranges[i].Start > ranges[i - 1].End, "数据块区间不能重叠");

        // 40510 夹在 IMEI(40501~40509) 与 MD5(40511~40518) 之间，不能被任何数据块覆盖
        Assert.DoesNotContain(ranges, r => RegisterMap.OUTDOOR_ACTIVATE_ADDR >= r.Start
                                           && RegisterMap.OUTDOOR_ACTIVATE_ADDR <= r.End);
    }

    [Fact]
    public void EncodeImei_WritesAsciiHighByteFirst()
    {
        // 与 MainForm.DecodeAscii 的读法对称：寄存器内高字节在前
        bool success = MainForm.TryBuildBlockWords(RegisterMap.IMEI_ADDR, "86", out ushort[] words, out string error);

        Assert.True(success, error);
        Assert.Equal(9, words.Length);
        Assert.Equal((ushort)0x3836, words[0]); // '8' = 0x38, '6' = 0x36
        Assert.Equal((ushort)0x0000, words[1]);
        Assert.All(words.Skip(1), word => Assert.Equal((ushort)0, word));
    }

    [Fact]
    public void EncodeImei_RoundTripsThroughDecode()
    {
        const string imei = "861100000000000";
        Assert.True(MainForm.TryBuildBlockWords(RegisterMap.IMEI_ADDR, imei, out ushort[] words, out string error), error);

        Assert.Equal(imei, MainForm.FormatBlockWords(RegisterMap.IMEI_ADDR, words));
    }

    [Fact]
    public void EncodeImei_RejectsContentLongerThan18Bytes()
    {
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.IMEI_ADDR, new string('1', 19), out _, out string error);

        Assert.False(success);
        Assert.Contains("内容过长", error);
    }

    [Fact]
    public void EncodeImei_RejectsNonAsciiCharacters()
    {
        bool success = MainForm.TryBuildBlockWords(RegisterMap.IMEI_ADDR, "8611 中国", out _, out string error);

        Assert.False(success);
        Assert.Contains("ASCII", error);
    }

    [Fact]
    public void EncodeUnitMode_SingleModeIsEighteenZeroCharacters()
    {
        // 协议：单机模式下 Mixpad 将该寄存器字段全部写 0x30（ASCII '0'）
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.UNIT_MODE_ADDR, new string('0', 18), out ushort[] words, out string error);

        Assert.True(success, error);
        Assert.Equal(9, words.Length);
        Assert.All(words, word => Assert.Equal((ushort)0x3030, word));
    }

    [Fact]
    public void EncodeMd5_Accepts32HexCharsWithLowByteFirst()
    {
        const string hex = "00112233445566778899aabbccddeeff";
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, hex, out ushort[] words, out string error);

        Assert.True(success, error);
        Assert.Equal(8, words.Length);
        // 寄存器内低字节在前：byte0=0x00, byte1=0x11 → 0x1100
        Assert.Equal((ushort)0x1100, words[0]);
        Assert.Equal((ushort)0xFFEE, words[7]);

        // 回读格式化按同样规则还原十六进制
        Assert.Equal(hex, MainForm.FormatBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, words).Replace(" ", ""));
    }

    [Fact]
    public void EncodeMd5_Accepts16DigitImeiAndComputesDigest()
    {
        const string imei = "861100000000000";
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, imei, out ushort[] words, out string error);

        Assert.True(success, error);

        byte[] expected = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.ASCII.GetBytes(imei));
        var rebuilt = new byte[16];
        for (int i = 0; i < 8; i++)
        {
            rebuilt[i * 2] = (byte)words[i];
            rebuilt[i * 2 + 1] = (byte)(words[i] >> 8);
        }
        Assert.Equal(expected, rebuilt);
    }

    [Fact]
    public void EncodeMd5_AcceptsUppercaseAndSeparators()
    {
        const string spaced = "00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF";
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, spaced, out ushort[] words, out string error);

        Assert.True(success, error);
        Assert.Equal((ushort)0x1100, words[0]);
        Assert.Equal("00112233445566778899aabbccddeeff",
            MainForm.FormatBlockWords(RegisterMap.ACTIVATION_MD5_START_ADDR, words).Replace(" ", ""));
    }

    [Fact]
    public void EncodeMd5_RejectsWrongHexLength()
    {
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, "0011", out _, out string error);

        Assert.False(success);
        Assert.Contains("长度不对", error);
        Assert.Contains("32 个字符", error);
    }

    [Fact]
    public void EncodeMd5_RejectsNonHexCharacters()
    {
        bool success = MainForm.TryBuildBlockWords(
            RegisterMap.ACTIVATION_MD5_START_ADDR, new string('z', 32), out _, out string error);

        Assert.False(success);
        Assert.Contains("非十六进制", error);
    }

    [Theory]
    [InlineData(40501)]
    [InlineData(40511)]
    [InlineData(40519)]
    public void BlockWriteHints_AgreeWithRegisterCount(ushort address)
    {
        // 对话框提示文案里的"寄存器数/字节数"必须和定义里的 RegisterCount 一致，
        // 否则用户按提示算出来的长度和实际写入的对不上。
        Assert.True(MainForm.TryGetBlockWriteDefinition(address, out var definition));
        Assert.Contains($"{definition!.RegisterCount} 寄存器", definition.Hint);
        Assert.Contains($"{definition.RegisterCount * 2} 字节", definition.Hint);
    }

    [Fact]
    public void BlockWriteConfirm_ImeiListsRangeReadableValueAndRegisters()
    {
        const string imei = "861187654321098";
        Assert.True(MainForm.TryBuildBlockWords(40501, imei, out ushort[] words, out string error), error);

        string text = MainForm.BuildBlockWriteConfirmText(40501, words);

        Assert.Contains("寄存器：40501~40509（9 个寄存器，0x10 整段写入）", text);
        Assert.Contains($"写入值：{imei}", text);
        Assert.Contains("寄存器值：0x3836", text);      // '8''6'
        Assert.Contains("高危段", text);
        Assert.Contains("确认写入？", text);
    }

    [Fact]
    public void BlockWriteConfirm_Md5ListsEightRegisters()
    {
        Assert.True(MainForm.TryBuildBlockWords(
            40511, "00112233445566778899aabbccddeeff", out ushort[] words, out string error), error);

        string text = MainForm.BuildBlockWriteConfirmText(40511, words);

        Assert.Contains("寄存器：40511~40518（8 个寄存器，0x10 整段写入）", text);
        Assert.Contains("00112233445566778899aabbccddeeff", text.Replace(" ", ""));
    }

    [Fact]
    public void BlockWriteConfirm_UnitModeListsNineRegisters()
    {
        Assert.True(MainForm.TryBuildBlockWords(
            40519, new string('0', 18), out ushort[] words, out string error), error);

        string text = MainForm.BuildBlockWriteConfirmText(40519, words);

        Assert.Contains("寄存器：40519~40527（9 个寄存器，0x10 整段写入）", text);
        Assert.Contains("写入值：000000000000000000", text);
    }

    [Fact]
    public void BlockWriteConfirm_EmptyWordsIsEmpty()
    {
        // 没解析出寄存器块时不该给出半截确认文案
        Assert.Equal("", MainForm.BuildBlockWriteConfirmText(40501, Array.Empty<ushort>()));
        // 非数据块地址也返回空
        Assert.Equal("", MainForm.BuildBlockWriteConfirmText(40001, new ushort[] { 1 }));
    }

    [Fact]
    public void BlockWriteOperation_IsWriteForReadWriteSegments()
    {
        // 405xx 是读写段（协议），按钮与标题用"写入"
        Assert.Equal("写入", MainForm.DescribeBlockWriteOperation(40501));
        Assert.Equal("写入", MainForm.DescribeBlockWriteOperation(40511));
        Assert.Equal("写入", MainForm.DescribeBlockWriteOperation(40519));
    }

    [Fact]
    public void ValueMeanings_OutdoorActivateListsAllThreeStates()
    {
        string text = MainForm.FormatValueMeaningList(RegisterMap.OUTDOOR_ACTIVATE_ADDR);

        Assert.Contains("0 = 未激活", text);
        Assert.Contains("1 = 允许使用", text);
        Assert.Contains("2 = 禁止使用", text);
    }

    [Fact]
    public void ValueMeanings_MatchProtocolWordingForActivation()
    {
        // 协议原文：0x0000 未激活（默认）/ 0x0001 允许使用（已激活）/ 0x0002 禁止使用（已激活）
        Assert.Contains("未激活", MainForm.DescribeWriteValueMeaning(40510, 0));
        Assert.Contains("本次 1 = 允许使用", MainForm.DescribeWriteValueMeaning(40510, 1));
        Assert.Contains("本次 2 = 禁止使用", MainForm.DescribeWriteValueMeaning(40510, 2));
    }

    [Theory]
    [InlineData(40601)]
    [InlineData(40674)]
    [InlineData(40801)]
    [InlineData(40851)]
    [InlineData(40858)]
    public void OtaAndFlashSegments_AreDisplayedReadOnly(ushort address)
    {
        // 协议标注读写，界面按只读展示：不允许写入入口
        Assert.Equal(RegisterAccess.ReadWrite, RegisterMap.GetAccess(address));
        Assert.True(RegisterMap.IsDisplayOnlyReadSegment(address));
        Assert.False(RegisterMap.IsWritableInUi(address));
    }

    [Theory]
    [InlineData(40501)]
    [InlineData(40510)]
    [InlineData(40511)]
    [InlineData(40519)]
    public void InfoSection_IsWritableInUi(ushort address)
    {
        Assert.True(RegisterMap.IsWritableInUi(address));
        Assert.False(RegisterMap.IsDisplayOnlyReadSegment(address));
    }

    [Theory]
    [InlineData(40401)]
    [InlineData(40402)]
    public void FactoryTestEntries_AreNotWritableInUi(ushort address)
    {
        // 已从界面移除（不显示、也没有写入入口），仅保留协议分类
        Assert.False(RegisterMap.IsWritableInUi(address));
        Assert.True(RegisterMap.IsHighRiskWriteSegment(address));
    }

    [Fact]
    public void DeviceSegments_RemainReadOnlyInUi()
    {
        // 风盘/地暖/底壳段落在 40001~49999 内，但寄存器在各从机上，界面按只读
        Assert.False(RegisterMap.IsWritableInUi(41001));
        Assert.False(RegisterMap.IsWritableInUi(42001));
        Assert.False(RegisterMap.IsWritableInUi(43001));
    }
}
