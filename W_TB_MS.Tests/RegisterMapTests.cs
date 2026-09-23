using System.Reflection;
using W_TB_MS.Models;
using Xunit;

namespace W_TB_MS.Tests;

public class RegisterMapTests
{
    [Theory]
    [InlineData(30106, RegisterAccess.ReadOnly)]
    [InlineData(40001, RegisterAccess.ReadWrite)]
    [InlineData(40105, RegisterAccess.WriteOnly)]
    [InlineData(40201, RegisterAccess.ReadWrite)]
    [InlineData(40308, RegisterAccess.ReadWrite)]
    public void GetAccess_MatchesProtocolSections(ushort address, RegisterAccess expected)
    {
        Assert.Equal(expected, RegisterMap.GetAccess(address));
    }

    [Fact]
    public void AllRegisterAddresses_AreUnique()
    {
        var fields = typeof(RegisterMap)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ushort) && f.Name.EndsWith("_ADDR", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(fields);

        var addresses = new HashSet<ushort>();
        foreach (var field in fields)
        {
            var addr = (ushort)field.GetValue(null)!;
            Assert.True(addresses.Add(addr), $"寄存器地址重复: {field.Name} = {addr}");
        }
    }

    [Fact]
    public void FaultCodeMap_CoversAllProtocolFaultCodes()
    {
        Assert.All(Enumerable.Range(1, 66), code => Assert.True(RegisterMap.FaultCodeMap.ContainsKey(code), $"缺少故障码: {code}"));
        Assert.Equal("驱动板和主控板通信故障", RegisterMap.FaultCodeMap[41]);
        Assert.Equal("激活变未激活", RegisterMap.FaultCodeMap[100]);
        Assert.Equal(RegisterMap.FaultCodeMap.Keys.Order(), RegisterMap.FaultResetMethodMap.Keys.Order());
    }

    [Fact]
    public void External4GAddressHelpers_MatchSpreadsheetFormula()
    {
        Assert.Equal((ushort)20060, RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_VALVE_OFFSET));
        Assert.Equal((ushort)20911, RegisterMap.GetFan4GAddress(15, RegisterMap.FAN_4G_MODEL_OFFSET));
        Assert.Equal((ushort)21001, RegisterMap.GetManifold4GStatusAddress(1));
        Assert.Equal((ushort)21015, RegisterMap.GetManifold4GStatusAddress(15));
        Assert.Equal((ushort)30350, RegisterMap.GetFaultHistoryRecordAddress(10));
    }

    [Fact]
    public void BitDefinitions_MatchSpreadsheetDefinedBits()
    {
        Assert.Equal(16, RegisterMap.FaultReg1Bits.Count);
        Assert.Equal(16, RegisterMap.FaultReg2Bits.Count);
        Assert.Equal(16, RegisterMap.FaultReg3Bits.Count);
        Assert.Equal(16, RegisterMap.FaultReg4Bits.Count);
        Assert.Equal(new[] { 0, 1, 2, 15 }, RegisterMap.FaultReg5Bits.Keys.Order());
        Assert.Equal("冻机预警", RegisterMap.FaultReg5Bits[2]);
        Assert.Equal(Enumerable.Range(0, 16), RegisterMap.StatusWordBits.Keys.Order());
        Assert.Equal(Enumerable.Range(0, 16), RegisterMap.DeviceStatusBits.Keys.Order());
        Assert.Equal(Enumerable.Range(0, 11), RegisterMap.DipSwitchBits.Keys.Order());
    }

    [Fact]
    public void CurveBitDefinitions_Have110UniqueAddressBitPairs()
    {
        var pairs = new List<(ushort Address, int Bit)>();

        void Add(ushort address, IEnumerable<int> bits)
        {
            pairs.AddRange(bits.Select(bit => (address, bit)));
        }

        Add(30101, RegisterMap.FaultReg1Bits.Keys);
        Add(30102, RegisterMap.FaultReg2Bits.Keys);
        Add(30103, RegisterMap.FaultReg3Bits.Keys);
        Add(30104, RegisterMap.FaultReg4Bits.Keys);
        Add(30105, RegisterMap.FaultReg5Bits.Keys);
        Add(30106, RegisterMap.StatusWordBits.Keys);
        Add(30201, RegisterMap.DeviceStatusBits.Keys);
        Add(30229, RegisterMap.DipSwitchBits.Keys);

        Assert.Equal(111, pairs.Count);
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void CurveBitPages_SeparateFaultAndStatusRegisters()
    {
        Assert.Equal(
            new ushort[] { 30101, 30102, 30103, 30104, 30105 },
            MainForm.FaultCurveAddresses);
        Assert.Equal(
            new ushort[] { 30106, 30201, 30229, 40201, 40202, 40212, 30990, 30991 },
            MainForm.StatusCurveAddresses);
        Assert.Empty(MainForm.FaultCurveAddresses.Intersect(MainForm.StatusCurveAddresses));
    }

    [Fact]
    public void NumericCurveDefinitions_CoverAll39ProtocolValues()
    {
        IReadOnlyList<ushort> addresses = W_TB_MS.MainForm.NumericCurveAddresses;

        Assert.Equal(39, addresses.Count);
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
        Assert.Contains((ushort)30108, addresses);
        Assert.Contains((ushort)30217, addresses);
        Assert.Contains((ushort)30235, addresses);
        Assert.Contains((ushort)30241, addresses);
        Assert.Contains((ushort)30242, addresses);
    }

    [Fact]
    public void CurveSelectors_HaveNoDefaultSelection()
    {
        Assert.Equal(0, W_TB_MS.MainForm.DefaultCurveSelectionCount);
    }

    [Fact]
    public void ComputeRuntimeModeEnum_MapsStatusWordCorrectly()
    {
        // 运行模式1：30106 bit3~5
        // bit3~5=000 → 7=--
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_NONE, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b0000 }));
        // bit3~5=001 → 1=制冷待机
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_COOLING_STANDBY, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b00_1000 }));
        // bit3~5=010 → 2=制冷报警停机
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_COOLING_ALARM_STOP, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b01_0000 }));
        // bit3~5=011 → 7=--
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_NONE, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b01_1000 }));
        // bit3~5=100 → 3=制热待机
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_HEATING_STANDBY, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b10_0000 }));
        // bit3~5=101 → 4=制热报警停机
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_HEATING_ALARM_STOP, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b10_1000 }));
        // bit3~5=110 → 5=除霜中
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_DEFROSTING, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b11_0000 }));
        // bit3~5=111 → 6=压缩机预热
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_PREHEATING, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort> { [30106] = 0b11_1000 }));
        // 30106 缺失 → 0
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE_DATA_MISSING, W_TB_MS.MainForm.ComputeRuntimeModeEnum(new Dictionary<ushort, ushort>()));
    }

    [Fact]
    public void ComputeRuntimeMode2Enum_MapsStatusWordCorrectly()
    {
        // 运行模式2：30106 bit3~5 + bit6，热水相关模式还需 30223=1
        // bit3~5=000 + bit6=0 → 1=制冷
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COOLING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b0000 }));
        // bit3~5=000 + bit6=1 + 30223=1 → 2=制冷+热水
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COOLING_HOT_WATER, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b100_0000, [30223] = 1 }));
        // bit3~5=000 + bit6=1 + 30223=0 → 1=制冷（不显示热水）
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COOLING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b100_0000, [30223] = 0 }));
        // bit3~5=000 + bit6=1 + 30223 缺失 → 1=制冷（不显示热水）
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COOLING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b100_0000 }));
        // bit3~5=011 + bit6=0 → 3=制热
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_HEATING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b01_1000 }));
        // bit3~5=011 + bit6=1 + 30223=1 → 4=制热+热水
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_HEATING_HOT_WATER, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b101_1000, [30223] = 1 }));
        // bit3~5=011 + bit6=1 + 30223=0 → 3=制热（不显示热水）
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_HEATING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b101_1000, [30223] = 0 }));
        // bit3~5=110（除霜）+ bit6=1 + 30223=1 → 5=热水
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_HOT_WATER, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b111_0000, [30223] = 1 }));
        // bit3~5=110（除霜）+ bit6=1 + 30223=0 → 6=压机未运行（不显示热水）
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COMPRESSOR_OFF, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b111_0000, [30223] = 0 }));
        // bit3~5=111（预热）+ bit6=0 → 6=压机未运行
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_COMPRESSOR_OFF, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort> { [30106] = 0b11_1000 }));
        // 30106 缺失 → 0
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE_DATA_MISSING, W_TB_MS.MainForm.ComputeRuntimeMode2Enum(new Dictionary<ushort, ushort>()));
    }

    [Fact]
    public void RuntimeModeMeanings_CoversAllEnumValues()
    {
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5, 6, 7 }, W_TB_MS.MainForm.RuntimeModeMeanings.Keys.Order().ToArray());
        Assert.Equal(new ushort[] { 0, 1, 2, 3, 4, 5, 6 }, W_TB_MS.MainForm.RuntimeMode2Meanings.Keys.Order().ToArray());
    }

    [Fact]
    public void DeriveModeRegisters_InjectsRuntimeModeVirtualAddress()
    {
        var values = new Dictionary<ushort, ushort> { [30106] = (0b101 << 3) | 0x40, [30223] = 1 };

        W_TB_MS.MainForm.DeriveModeRegisters(values);

        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE1_HEATING_ALARM_STOP, values[W_TB_MS.MainForm.RUNTIME_MODE_CURVE_ADDR]);
        Assert.Equal(W_TB_MS.MainForm.RUNTIME_MODE2_HOT_WATER, values[W_TB_MS.MainForm.RUNTIME_MODE2_CURVE_ADDR]);
        // 虚拟地址不得进入轮询采样地址集合
        Assert.DoesNotContain(W_TB_MS.MainForm.RUNTIME_MODE_CURVE_ADDR, W_TB_MS.MainForm.RequiredCurveSampleAddresses);
        Assert.DoesNotContain(W_TB_MS.MainForm.RUNTIME_MODE2_CURVE_ADDR, W_TB_MS.MainForm.RequiredCurveSampleAddresses);
    }
}
