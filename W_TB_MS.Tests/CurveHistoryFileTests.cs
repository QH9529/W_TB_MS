using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using W_TB_jiankong.Models;
using Xunit;

namespace W_TB_jiankong.Tests;

public class CurveHistoryFileTests
{
    [Fact]
    public void LogFile_RoundTripsCurveData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"curve-{Guid.NewGuid():N}.log");
        try
        {
            CurveHistoryData source = CreateSample();
            CurveHistoryFile.SaveLog(path, source);
            CurveHistoryData loaded = CurveHistoryFile.LoadLog(path);
            AssertEquivalent(source, loaded);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExcelFile_RoundTripsCurveData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"curve-{Guid.NewGuid():N}.xlsx");
        try
        {
            CurveHistoryData source = CreateSample();
            CurveHistoryFile.SaveExcel(path, source);
            CurveHistoryData loaded = CurveHistoryFile.LoadExcel(path);
            AssertEquivalent(source, loaded);
            using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
            Assert.Empty(new OpenXmlValidator().Validate(document));
            IReadOnlyList<Sheet> sheets = document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ToList();
            Assert.Single(sheets);
            Assert.Equal("曲线数据", sheets[0].Name?.Value);
            WorksheetPart worksheetPart = (WorksheetPart)document.WorkbookPart.GetPartById(sheets[0].Id!.Value!);
            Row pageRow = worksheetPart.Worksheet.GetFirstChild<SheetData>()!.Elements<Row>().First();
            Assert.Contains("空调进水温度", pageRow.InnerText);
            Assert.Contains("热泵主机开关机状态", pageRow.InnerText);
            Assert.Contains("冬季防冻保护", pageRow.InnerText);
            Assert.DoesNotContain("参数页", pageRow.InnerText);
            Assert.DoesNotContain("30106 BIT0", pageRow.InnerText);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static CurveHistoryData CreateSample()
    {
        return new CurveHistoryData
        {
            Times = new List<DateTime>
            {
                new(2026, 9, 4, 10, 0, 0, 123),
                new(2026, 9, 4, 10, 0, 2, 456)
            },
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
                    Values = new List<double> { 25.1, 25.2 }
                },
                new()
                {
                    Key = "B:30106:0",
                    Kind = CurveSeriesKind.Bit,
                    Address = 30106,
                    Bit = 0,
                    GroupName = "30106 主机运行状态",
                    Name = "热泵主机开关机状态",
                    ZeroText = "关机",
                    OneText = "开机",
                    Values = new List<double> { 0, 1 }
                },
                new()
                {
                    Key = "B:30101:0",
                    Kind = CurveSeriesKind.Bit,
                    Address = 30101,
                    Bit = 0,
                    GroupName = "故障报警一",
                    Name = "冬季防冻保护",
                    ZeroText = "正常",
                    OneText = "触发",
                    Values = new List<double> { 0, 0 }
                }
            }
        };
    }

    private static void AssertEquivalent(CurveHistoryData expected, CurveHistoryData actual)
    {
        Assert.Equal(expected.Times.Select(time => time.ToOADate()), actual.Times.Select(time => time.ToOADate()));
        Assert.Equal(expected.Series.Count, actual.Series.Count);
        for (int index = 0; index < expected.Series.Count; index++)
        {
            CurveHistorySeries expectedSeries = expected.Series[index];
            CurveHistorySeries actualSeries = actual.Series[index];
            Assert.Equal(expectedSeries.Key, actualSeries.Key);
            Assert.Equal(expectedSeries.Name, actualSeries.Name);
            Assert.Equal(expectedSeries.GroupName, actualSeries.GroupName);
            Assert.Equal(expectedSeries.Unit, actualSeries.Unit);
            Assert.Equal(expectedSeries.Values, actualSeries.Values);
        }
    }
}
