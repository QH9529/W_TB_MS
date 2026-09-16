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
}
