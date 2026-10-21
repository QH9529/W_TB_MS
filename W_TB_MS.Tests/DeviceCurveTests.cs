using System.Collections.Generic;
using System.Linq;
using W_TB_MS.Models;
using Xunit;

namespace W_TB_MS.Tests;

/// <summary>
/// 设备侧曲线（风盘/地暖）按在线位图生成的相关测试。
///
/// 关键约束：未在线风盘的模板区不参与轮询（MainForm.BuildPollBlocks 只为在线风盘追加请求块），
/// 所以绝不能把它们写进静态曲线定义——否则 CanRecordCurveSample 会因为读不到地址
/// 而让整条样本永远无法落库，所有曲线一起停摆。
/// </summary>
public class DeviceCurveTests
{
    [Fact]
    public void BuildFanCurveDefinitions_OnlyCoversOnlineFans()
    {
        // bit1、bit2 在线 → 风盘 1、风盘 2
        List<MainForm.NumericCurveDefinition> definitions = MainForm.BuildFanCurveDefinitions(0b110);

        Assert.Equal(12, definitions.Count); // 2 台 × 6 项
        Assert.All(definitions, item => Assert.Equal(MainForm.FanCurveGroupName, item.GroupName));
        Assert.All(definitions, item => Assert.StartsWith("风盘", item.Name));
        Assert.DoesNotContain(definitions, item => item.Name.StartsWith("风盘3"));
    }

    [Fact]
    public void BuildFanCurveDefinitions_IsEmptyWithoutOnlineFans()
    {
        Assert.Empty(MainForm.BuildFanCurveDefinitions(0));
        Assert.Empty(MainForm.BuildFanCurveDefinitions(1)); // bit0 是保留位，不算任何风盘
    }

    [Fact]
    public void BuildFanCurveDefinitions_MapsProtocolOffsets()
    {
        List<MainForm.NumericCurveDefinition> definitions = MainForm.BuildFanCurveDefinitions(0b10); // 只有风盘1

        Assert.Equal(6, definitions.Count);
        // 地址必须落在风盘1 的模板区（20000 + 60×1 = 20060 起）
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_TEMPERATURE_OFFSET)
            && item.Name.EndsWith("温度"));
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_HUMIDITY_OFFSET)
            && item.Name.EndsWith("湿度"));
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_CO2_OFFSET)
            && item.Name.EndsWith("CO2浓度"));
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_PM25_OFFSET)
            && item.Name.EndsWith("PM2.5浓度"));
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_SPEED_OFFSET)
            && item.Name.EndsWith("风盘风速"));
        Assert.Contains(definitions, item =>
            item.Address == RegisterMap.GetFan4GAddress(1, RegisterMap.FAN_4G_FRESH_AIR_OFFSET)
            && item.Name.EndsWith("新风风速"));
    }

    [Fact]
    public void BuildFanCurveDefinitions_DecodesTemperatureAsSignedTenths()
    {
        // 协议：风盘温度 -1000～1000，实际 -100～100℃，精度 0.1，传输值 = 实际值×10
        MainForm.NumericCurveDefinition temperature = MainForm.BuildFanCurveDefinitions(0b10)
            .Single(item => item.Name.EndsWith("温度"));

        Assert.Equal(MainForm.NumericCurveValueKind.SignedTenths, temperature.ValueKind);
        Assert.Equal("℃", temperature.Unit);
    }

    [Fact]
    public void BuildManifoldCurveDefinitions_CoversEightValvesPerOnlineManifold()
    {
        // bit1、bit3 在线 → 分集水器 1、分集水器 3
        List<MainForm.BitCurveDefinition> definitions = MainForm.BuildManifoldCurveDefinitions(0b1010);

        Assert.Equal(16, definitions.Count); // 2 台 × 8 路阀门
        Assert.All(definitions, item => Assert.Equal(MainForm.ManifoldCurveGroupName, item.GroupName));
        Assert.All(definitions, item => Assert.Equal("关", item.ZeroText));
        Assert.All(definitions, item => Assert.Equal("开", item.OneText));
        Assert.DoesNotContain(definitions, item => item.Name.StartsWith("分集水器2"));

        // 分集水器1 的 8 路阀门共用一个状态字（21001），位号 0~7
        ushort[] addresses = definitions
            .Where(item => item.Name.StartsWith("分集水器1"))
            .Select(item => item.Address)
            .Distinct()
            .ToArray();
        Assert.Single(addresses);
        Assert.Equal(RegisterMap.GetManifold4GStatusAddress(1), addresses[0]);
        Assert.Equal(
            Enumerable.Range(0, 8),
            definitions.Where(item => item.Name.StartsWith("分集水器1")).Select(item => item.Bit).OrderBy(bit => bit));
    }

    [Fact]
    public void BuildManifoldCurveDefinitions_IsEmptyWithoutOnlineManifolds()
    {
        Assert.Empty(MainForm.BuildManifoldCurveDefinitions(0));
        Assert.Empty(MainForm.BuildManifoldCurveDefinitions(1)); // bit0 保留
    }

    [Fact]
    public void FanCurveAddresses_AreUniqueAndOutsideHostCurves()
    {
        ushort[] fanAddresses = MainForm.BuildFanCurveDefinitions(0xFFFF)
            .Select(item => item.Address)
            .ToArray();

        // 数值曲线按地址索引绘图数据，地址必须唯一
        Assert.Equal(fanAddresses.Length, fanAddresses.Distinct().Count());
        // 全部落在外机-4G 风盘模板区（20000 + 60×1 起，最多到 20000 + 60×15 + 20）
        Assert.All(fanAddresses, address => Assert.InRange(
            address,
            RegisterMap.GetFan4GBaseAddress(1),
            (ushort)(RegisterMap.GetFan4GBaseAddress(RegisterMap.FAN_4G_MAX_COUNT) + RegisterMap.FAN_4G_TEMPLATE_COUNT - 1)));
        // 与主机参数曲线不重叠
        Assert.Empty(MainForm.NumericCurveAddresses.Intersect(fanAddresses));
    }

    [Fact]
    public void ManifoldCurveAddresses_StayInsideManifoldStatusSegment()
    {
        ushort[] addresses = MainForm.BuildManifoldCurveDefinitions(0xFFFF)
            .Select(item => item.Address)
            .Distinct()
            .ToArray();

        Assert.Equal(RegisterMap.MANIFOLD_MAX_COUNT, addresses.Length);
        Assert.All(addresses, address => Assert.InRange(
            address,
            RegisterMap.MANIFOLD_STATUS_START_ADDR,
            RegisterMap.MANIFOLD_STATUS_END_ADDR));
    }

    [Fact]
    public void ManifoldCurves_AddressPlusBitKeysAreUnique()
    {
        // BIT 曲线按 (地址, 位) 索引绘图对象，键必须唯一
        var keys = MainForm.BuildManifoldCurveDefinitions(0xFFFF)
            .Select(item => (item.Address, item.Bit))
            .ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }
}
