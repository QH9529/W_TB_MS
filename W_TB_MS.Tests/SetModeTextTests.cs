using Xunit;
using W_TB_MS.Models;

namespace W_TB_MS.Tests;

/// <summary>
/// 顶部状态栏"设置模式"显示规则测试：
/// 40001=1 时判断 40201 显示制冷/制热；40001=0 时不判断 40201。
/// 40202=1 时显示热水；40202=0 时不显示热水。
/// 40001 和 40202 都是 0 时才显示"关机"；40001=0 且 40202=1 显示"热水"。
/// </summary>
public class SetModeTextTests
{
    private const ushort OnOff = RegisterMap.ON_OFF_ADDR;            // 40001
    private const ushort WorkMode = RegisterMap.SET_WORK_MODE_ADDR;  // 40201: 1=制冷, 2=制热
    private const ushort HotWater = RegisterMap.HOT_WATER_ENABLE_ADDR; // 40202

    [Fact]
    public void PowerOn_CoolingMode_NoHotWater_ShowsCooling()
    {
        var values = new Dictionary<ushort, ushort> { [OnOff] = 1, [WorkMode] = 1, [HotWater] = 0 };
        Assert.Equal("制冷", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void PowerOn_CoolingMode_WithHotWater_ShowsCoolingPlusHotWater()
    {
        var values = new Dictionary<ushort, ushort> { [OnOff] = 1, [WorkMode] = 1, [HotWater] = 1 };
        Assert.Equal("制冷+热水", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void PowerOn_HeatingMode_NoHotWater_ShowsHeating()
    {
        var values = new Dictionary<ushort, ushort> { [OnOff] = 1, [WorkMode] = 2, [HotWater] = 0 };
        Assert.Equal("制热", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void PowerOn_HeatingMode_WithHotWater_ShowsHeatingPlusHotWater()
    {
        var values = new Dictionary<ushort, ushort> { [OnOff] = 1, [WorkMode] = 2, [HotWater] = 1 };
        Assert.Equal("制热+热水", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void PowerOff_BothZero_ShowsShutdown()
    {
        // 40001=0 且 40202=0 → 关机
        var values = new Dictionary<ushort, ushort> { [OnOff] = 0, [WorkMode] = 1, [HotWater] = 0 };
        Assert.Equal("关机", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void PowerOff_HotWaterEnabled_ShowsHotWaterOnly()
    {
        // 40001=0 不判断 40201（不显示制冷/制热），但 40202=1 显示"热水"
        var values = new Dictionary<ushort, ushort> { [OnOff] = 0, [WorkMode] = 1, [HotWater] = 1 };
        Assert.Equal("热水", W_TB_MS.MainForm.SetModeText(values));
    }

    [Fact]
    public void MissingData_ShowsDataMissing()
    {
        Assert.Equal("数据缺失", W_TB_MS.MainForm.SetModeText(new Dictionary<ushort, ushort>()));
    }
}
