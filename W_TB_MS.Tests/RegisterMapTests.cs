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
            new ushort[] { 30106, 30201, 30229 },
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
}
