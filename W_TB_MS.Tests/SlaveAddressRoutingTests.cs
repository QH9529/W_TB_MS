using W_TB_MS.Modbus;
using W_TB_MS.Models;
using Xunit;

namespace W_TB_MS.Tests;

/// <summary>
/// 从机地址路由：风盘(0x91~0x9F)/地暖(0xA1~0xAF)/MixPad 底壳(0x51~0x5F)
/// 的寄存器不在热泵主机(0xF1~0xF4)上，必须按显式从机地址寻址。
/// 这里锁定 ModbusRtuClient 的"显式从机地址"重载构造出的帧布局，
/// 与模拟外机(temp/sim_odu.py)自检中的实际收发报文一致。
/// </summary>
public class SlaveAddressRoutingTests
{
    private const byte PcMaster = 0x5F;   // PC 模拟上位机地址
    private const byte HeatPump = 0xF1;
    private const byte FanCoil = 0x91;    // 风盘 1
    private const byte FloorHeating = 0xA1; // 地暖 1

    [Fact]
    public void BuildReadRequestFrame_ForFloorHeatingSlave_UsesSlave0xA1()
    {
        // 42001 = 0xA411（寄存器编号即 PDU 地址，不减 1）
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(PcMaster, FloorHeating, 0x03, 42001, 8);

        Assert.Equal(9, frame.Length);
        Assert.Equal(new byte[] { 0xA1, 0x5F, 0x03, 0xA4, 0x11, 0x00, 0x08 }, frame[..7]);
        ushort crc = ModbusRtuClient.CalculateCrc(frame, 7);
        Assert.Equal((byte)crc, frame[7]);
        Assert.Equal((byte)(crc >> 8), frame[8]);
    }

    [Fact]
    public void BuildReadRequestFrame_ForFanCoilSlave_UsesSlave0x91()
    {
        // 41001 = 0xA029
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(PcMaster, FanCoil, 0x04, 41001, 17);

        Assert.Equal(new byte[] { 0x91, 0x5F, 0x04, 0xA0, 0x29, 0x00, 0x11 }, frame[..7]);
        Assert.Equal((byte)ModbusRtuClient.CalculateCrc(frame, 7), frame[7]);
    }

    [Fact]
    public void BuildWriteSingleRegisterFrame_ForFloorHeatingValve_UsesSlave0xA1()
    {
        byte[] frame = ModbusRtuClient.BuildWriteSingleRegisterFrame(PcMaster, FloorHeating, 42002, 1);

        Assert.Equal(new byte[] { 0xA1, 0x5F, 0x06, 0xA4, 0x12, 0x00, 0x01 }, frame[..7]);
        Assert.Equal((byte)ModbusRtuClient.CalculateCrc(frame, 7), frame[7]);
    }

    [Fact]
    public void BuildWriteMultipleRegistersFrame_ForFloorHeating_UsesSlave0xA1()
    {
        byte[] frame = ModbusRtuClient.BuildWriteMultipleRegistersFrame(
            PcMaster, FloorHeating, 42001, new ushort[] { 1, 0, 1 });

        Assert.Equal(16, frame.Length);
        Assert.Equal(new byte[] { 0xA1, 0x5F, 0x10, 0xA4, 0x11, 0x00, 0x03, 0x06, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01 }, frame[..14]);
        Assert.Equal((byte)ModbusRtuClient.CalculateCrc(frame, 14), frame[14]);
        Assert.Equal((byte)(ModbusRtuClient.CalculateCrc(frame, 14) >> 8), frame[15]);
    }

    [Fact]
    public void BuildReadRequestFrame_ForMixPadShell_UsesSlave0x51()
    {
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(PcMaster, 0x51, 0x04, 33001, 1);

        Assert.Equal(new byte[] { 0x51, 0x5F, 0x04, 0x80, 0xE9, 0x00, 0x01 }, frame[..7]);
    }

    [Fact]
    public void SlaveAddressProperty_DefaultsToHeatPump()
    {
        using var client = new ModbusRtuClient();

        Assert.Equal(HeatPump, client.SlaveAddress);
        Assert.Equal(0x51, client.MasterAddress);
    }

    [Fact]
    public void SlaveAddressHint_MatchesRegisterSegments()
    {
        Assert.Equal("风盘设备（从机 0x91~0x9F）", RegisterMap.GetSlaveDeviceHint(41001));
        Assert.Equal("地暖设备（从机 0xA1~0xAF）", RegisterMap.GetSlaveDeviceHint(42001));
        Assert.Equal("MixPad 底壳（从机 0x51~0x5F）", RegisterMap.GetSlaveDeviceHint(43004));
        Assert.Null(RegisterMap.GetSlaveDeviceHint(40001));
    }

    [Fact]
    public void FloorHeatingValveAddresses_Cover42001To42100()
    {
        Assert.Equal((ushort)42001, RegisterMap.HEATER_VALVE1_ADDR);
        Assert.Equal((ushort)42100, RegisterMap.HEATER_ALL_VALVE_OFF_ADDR);
        Assert.Equal((ushort)43004, RegisterMap.HEATER_ONLINE_STATUS_ADDR);
    }
}
