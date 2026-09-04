using W_TB_jiankong.Models;
using Xunit;

namespace W_TB_jiankong.Tests;

public class CurveArchiveWriterTests
{
    [Fact]
    public void SavePair_CommitsReadableLogAndExcelWithoutTemporaryFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"curve-archive-{Guid.NewGuid():N}");
        try
        {
            var data = new CurveHistoryData
            {
                Times = new List<DateTime> { new(2026, 9, 5, 8, 30, 0) },
                Series = new List<CurveHistorySeries>
                {
                    new()
                    {
                        Key = "N:30209",
                        Kind = CurveSeriesKind.Numeric,
                        Address = 30209,
                        GroupName = "温度",
                        Name = "空调进水温度",
                        Unit = "℃",
                        Values = new List<double> { 25.6 }
                    }
                }
            };

            CurveArchivePair pair = CurveArchiveWriter.SavePair(folder, data);

            Assert.True(File.Exists(pair.LogPath));
            Assert.True(File.Exists(pair.ExcelPath));
            Assert.Equal(
                Path.GetFileNameWithoutExtension(pair.LogPath),
                Path.GetFileNameWithoutExtension(pair.ExcelPath));
            Assert.Single(CurveHistoryFile.LoadLog(pair.LogPath).Times);
            Assert.Single(CurveHistoryFile.LoadExcel(pair.ExcelPath).Times);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp-*"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SavePair_ExpandsCompactBitRegistersWithoutLosingValues()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"curve-archive-{Guid.NewGuid():N}");
        try
        {
            var data = new CompactCurveHistoryData
            {
                Times = new List<DateTime>
                {
                    new(2026, 9, 5, 8, 30, 0),
                    new(2026, 9, 5, 8, 30, 1),
                    new(2026, 9, 5, 8, 30, 2)
                },
                NumericSeries = new List<CurveHistorySeries>
                {
                    new()
                    {
                        Key = "N:30209",
                        Kind = CurveSeriesKind.Numeric,
                        Address = 30209,
                        Name = "空调进水温度",
                        Values = new List<double> { 25.1, 25.2, 25.3 }
                    }
                },
                BitSeries = new List<CurveHistorySeries>
                {
                    new()
                    {
                        Key = "B:30106:0",
                        Kind = CurveSeriesKind.Bit,
                        Address = 30106,
                        Bit = 0,
                        Name = "主机状态"
                    },
                    new()
                    {
                        Key = "B:30106:15",
                        Kind = CurveSeriesKind.Bit,
                        Address = 30106,
                        Bit = 15,
                        Name = "高位状态"
                    }
                },
                BitRegisters = new Dictionary<ushort, List<ushort>>
                {
                    [30106] = new() { 0x0000, 0x0001, 0x8001 }
                }
            };

            CurveArchivePair pair = CurveArchiveWriter.SavePair(folder, data);
            foreach (CurveHistoryData loaded in new[]
                     {
                         CurveHistoryFile.LoadLog(pair.LogPath),
                         CurveHistoryFile.LoadExcel(pair.ExcelPath)
                     })
            {
                Assert.Equal(new[] { 25.1, 25.2, 25.3 }, loaded.Series.Single(x => x.Key == "N:30209").Values);
                Assert.Equal(new[] { 0.0, 1.0, 1.0 }, loaded.Series.Single(x => x.Key == "B:30106:0").Values);
                Assert.Equal(new[] { 0.0, 0.0, 1.0 }, loaded.Series.Single(x => x.Key == "B:30106:15").Values);
            }
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
