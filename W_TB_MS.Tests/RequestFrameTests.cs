using W_TB_MS.Modbus;
using Xunit;

namespace W_TB_MS.Tests;

public class RequestFrameTests
{
    [Fact]
    public void SortPortNames_UsesNumericComSequence()
    {
        string[] ports = { "COM10", "COM3", "COM2", "COM21" };

        Assert.Equal(
            new[] { "COM2", "COM3", "COM10", "COM21" },
            ModbusRtuClient.SortPortNames(ports));
    }

    [Fact]
    public void BuildReadRequestFrame_Returns9Bytes_WithCorrectLayout()
    {
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(0x51, 0xF1, 0x04, 30101, 23);

        Assert.Equal(9, frame.Length);
        Assert.Equal(0xF1, frame[0]);
        Assert.Equal(0x51, frame[1]);
        Assert.Equal(0x04, frame[2]);
        Assert.Equal((byte)(30101 >> 8), frame[3]);
        Assert.Equal((byte)(30101 & 0xFF), frame[4]);
        Assert.Equal((byte)(23 >> 8), frame[5]);

        Assert.Equal((byte)(23 & 0xFF), frame[6]);
        ushort crc = ModbusRtuClient.CalculateCrc(frame, 7);
        Assert.Equal((byte)(crc & 0xFF), frame[7]);
        Assert.Equal((byte)(crc >> 8), frame[8]);
    }

    [Fact]
    public void BuildWriteSingleRegisterFrame_Returns9Bytes_WithCorrectCrc()
    {
        byte[] frame = ModbusRtuClient.BuildWriteSingleRegisterFrame(0x51, 0xF1, 40001, 1);

        Assert.Equal(new byte[] { 0xF1, 0x51, 0x06, 0x9C, 0x41, 0x00, 0x01 }, frame[..7]);
        ushort crc = ModbusRtuClient.CalculateCrc(frame, 7);
        Assert.Equal((byte)crc, frame[7]);
        Assert.Equal((byte)(crc >> 8), frame[8]);
    }

    [Fact]
    public void BuildReadRequestFrame_Matches4GCaptureLayout()
    {
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(0x51, 0xF1, 0x04, 0x7531, 4);

        Assert.Equal(new byte[] { 0xF1, 0x51, 0x04, 0x75, 0x31, 0x00, 0x04, 0x4D, 0x8E }, frame);
    }

    [Fact]
    public void BuildStatusRequestFrame_Matches4GCaptureLayout()
    {
        byte[] frame = ModbusRtuClient.BuildReadRequestFrame(0x51, 0xF1, 0x04, 30101, 6);

        Assert.Equal(new byte[] { 0xF1, 0x51, 0x04, 0x75, 0x95, 0x00, 0x06, 0x8D, 0xAC }, frame);
    }
}
