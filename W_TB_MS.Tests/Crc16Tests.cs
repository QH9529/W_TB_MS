using System.Text;
using W_TB_jiankong.Modbus;
using Xunit;

namespace W_TB_jiankong.Tests;

public class Crc16Tests
{
    [Fact]
    public void Crc16_StandardVector_123456789_Returns0x4B37()
    {
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        ushort crc = ModbusRtuClient.CalculateCrc(data, data.Length);
        Assert.Equal(0x4B37, crc);
    }

    [Fact]
    public void Crc16_EmptyData_Returns0xFFFF()
    {
        ushort crc = ModbusRtuClient.CalculateCrc(Array.Empty<byte>(), 0);
        Assert.Equal(0xFFFF, crc);
    }
}