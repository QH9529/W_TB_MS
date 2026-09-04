using System.Globalization;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace W_TB_jiankong.Models
{
    public enum CurveSeriesKind
    {
        Numeric,
        Bit
    }

    public sealed class CurveHistorySeries
    {
        public string Key { get; set; } = "";
        public CurveSeriesKind Kind { get; set; }
        public ushort Address { get; set; }
        public int? Bit { get; set; }
        public string GroupName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public bool UseRightAxis { get; set; }
        public string ZeroText { get; set; } = "";
        public string OneText { get; set; } = "";
        public List<double> Values { get; set; } = new();
    }

    public sealed class CurveHistoryData
    {
        public string Format { get; set; } = CurveHistoryFile.LogFormat;
        public int Version { get; set; } = 1;
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        public List<DateTime> Times { get; set; } = new();
        public List<CurveHistorySeries> Series { get; set; } = new();
    }

    internal sealed class CompactCurveHistoryData
    {
        internal DateTime ExportedAt { get; init; } = DateTime.Now;
        internal List<DateTime> Times { get; init; } = new();
        internal List<CurveHistorySeries> NumericSeries { get; init; } = new();
        internal List<CurveHistorySeries> BitSeries { get; init; } = new();
        internal Dictionary<ushort, List<ushort>> BitRegisters { get; init; } = new();

        internal IEnumerable<CurveHistorySeries> Series => NumericSeries.Concat(BitSeries);

        internal double GetValue(CurveHistorySeries series, int sampleIndex)
        {
            if (series.Kind == CurveSeriesKind.Numeric)
                return series.Values[sampleIndex];

            ushort word = BitRegisters[series.Address][sampleIndex];
            return (word & (1 << series.Bit!.Value)) == 0 ? 0.0 : 1.0;
        }
    }

    public static class CurveHistoryFile
    {
        public const string LogFormat = "W_TB_MS_CURVE_LOG";
        private const int ExcelDataStartRow = 8;
        private const uint HeaderStyleIndex = 1;
        private const uint DateStyleIndex = 2;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        public static void SaveLog(string path, CurveHistoryData data)
        {
            Validate(data);
            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, data, JsonOptions);
        }

        internal static void SaveLog(string path, CompactCurveHistoryData data)
        {
            Validate(data);
            using FileStream stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteString(nameof(CurveHistoryData.Format), LogFormat);
            writer.WriteNumber(nameof(CurveHistoryData.Version), 1);
            writer.WriteString(nameof(CurveHistoryData.ExportedAt), data.ExportedAt);
            writer.WritePropertyName(nameof(CurveHistoryData.Times));
            JsonSerializer.Serialize(writer, data.Times, JsonOptions);
            writer.WriteStartArray(nameof(CurveHistoryData.Series));
            foreach (CurveHistorySeries series in data.Series)
                WriteJsonSeries(writer, series, data.Times.Count, index => data.GetValue(series, index));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        public static CurveHistoryData LoadLog(string path)
        {
            using FileStream stream = File.OpenRead(path);
            CurveHistoryData? data = JsonSerializer.Deserialize<CurveHistoryData>(stream, JsonOptions);
            if (data is null)
                throw new InvalidDataException("LOG文件内容为空或格式无效。");
            Validate(data);
            return data;
        }

        public static void SaveExcel(string path, CurveHistoryData data)
        {
            Validate(data);
            SaveExcelCore(path, data.Times, data.Series, (series, index) => series.Values[index]);
        }

        internal static void SaveExcel(string path, CompactCurveHistoryData data)
        {
            Validate(data);
            SaveExcelCore(path, data.Times, data.Series, data.GetValue);
        }

        public static CurveHistoryData LoadExcel(string path)
        {
            using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
            WorkbookPart workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException("Excel工作簿结构无效。");
            List<string> sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable
                .Elements<SharedStringItem>()
                .Select(item => item.InnerText)
                .ToList() ?? new List<string>();
            var data = new CurveHistoryData { ExportedAt = File.GetLastWriteTime(path) };

            foreach (Sheet sheet in workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>())
            {
                if (sheet.Id?.Value is not string relationshipId
                    || workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
                    continue;

                if (!TryReadWorksheet(worksheetPart, sharedStrings, data, out List<DateTime> times))
                    continue;

                if (data.Times.Count == 0)
                {
                    data.Times = times;
                }
                else if (data.Times.Count != times.Count
                         || data.Times.Where((time, index) => time != times[index]).Any())
                {
                    throw new InvalidDataException($"工作表“{sheet.Name}”的时间列与其他曲线不一致。");
                }
            }

            if (data.Series.Count == 0)
                throw new InvalidDataException("Excel不是本软件导出的曲线文件，未找到曲线元数据。");

            Validate(data);
            return data;
        }

        public static CurveHistoryData Load(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                ? LoadExcel(path)
                : LoadLog(path);
        }

        private static void SaveExcelCore(
            string path,
            IReadOnlyList<DateTime> times,
            IEnumerable<CurveHistorySeries> allSeries,
            Func<CurveHistorySeries, int, double> getValue)
        {
            List<CurveHistorySeries> series = allSeries.ToList();
            using SpreadsheetDocument document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
            document.PackageProperties.Title = "W系列热泵曲线数据";
            document.PackageProperties.Subject = "实时曲线导出文件";

            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateStylesheet();
            stylesPart.Stylesheet.Save();

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            WriteWorksheet(workbookPart, sheets, 1, "数值曲线", times,
                series.Where(item => item.Kind == CurveSeriesKind.Numeric).ToList(), getValue);
            WriteWorksheet(workbookPart, sheets, 2, "BIT状态", times,
                series.Where(item => item.Kind == CurveSeriesKind.Bit).ToList(), getValue);
            workbookPart.Workbook.Save();
        }

        private static void WriteWorksheet(
            WorkbookPart workbookPart,
            Sheets sheets,
            uint sheetId,
            string sheetName,
            IReadOnlyList<DateTime> times,
            IReadOnlyList<CurveHistorySeries> series,
            Func<CurveHistorySeries, int, double> getValue)
        {
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            using (OpenXmlWriter writer = OpenXmlWriter.Create(worksheetPart))
            {
                writer.WriteStartElement(new Worksheet());
                writer.WriteElement(CreateFrozenSheetViews());
                writer.WriteStartElement(new Columns());
                writer.WriteElement(new Column { Min = 1, Max = 1, Width = 24, CustomWidth = true });
                if (series.Count > 0)
                {
                    writer.WriteElement(new Column
                    {
                        Min = 2,
                        Max = (uint)series.Count + 1,
                        Width = 22,
                        CustomWidth = true
                    });
                }
                writer.WriteEndElement();
                writer.WriteStartElement(new SheetData());

                for (uint rowIndex = 1; rowIndex < ExcelDataStartRow; rowIndex++)
                {
                    writer.WriteStartElement(new Row { RowIndex = rowIndex, Hidden = rowIndex > 1 });
                    if (rowIndex == 1)
                        WriteInlineStringCell(writer, 1, rowIndex, "时间", HeaderStyleIndex);
                    for (int index = 0; index < series.Count; index++)
                    {
                        CurveHistorySeries item = series[index];
                        string value = rowIndex switch
                        {
                            1 => $"[{FormatAddress(item)}] {item.Name}",
                            2 => item.Key,
                            3 => item.GroupName,
                            4 => item.Unit,
                            5 => item.UseRightAxis ? "R" : "L",
                            6 => item.ZeroText,
                            7 => item.OneText,
                            _ => ""
                        };
                        WriteInlineStringCell(writer, index + 2, rowIndex, value,
                            rowIndex == 1 ? HeaderStyleIndex : 0);
                    }
                    writer.WriteEndElement();
                }

                for (int sampleIndex = 0; sampleIndex < times.Count; sampleIndex++)
                {
                    uint rowIndex = (uint)(ExcelDataStartRow + sampleIndex);
                    writer.WriteStartElement(new Row { RowIndex = rowIndex });
                    WriteNumberCell(writer, 1, rowIndex, times[sampleIndex].ToOADate(), DateStyleIndex);
                    for (int seriesIndex = 0; seriesIndex < series.Count; seriesIndex++)
                    {
                        double value = getValue(series[seriesIndex], sampleIndex);
                        if (double.IsFinite(value))
                            WriteNumberCell(writer, seriesIndex + 2, rowIndex, value, 0);
                    }
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId,
                Name = sheetName
            });
        }

        private static SheetViews CreateFrozenSheetViews()
        {
            var pane = new Pane
            {
                HorizontalSplit = 1,
                VerticalSplit = 1,
                TopLeftCell = "B2",
                ActivePane = PaneValues.BottomRight,
                State = PaneStateValues.Frozen
            };
            return new SheetViews(new SheetView(pane) { WorkbookViewId = 0 });
        }

        private static Stylesheet CreateStylesheet()
        {
            var numberingFormats = new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy-mm-dd hh:mm:ss.000" })
            { Count = 1 };
            var fonts = new Fonts(
                new DocumentFormat.OpenXml.Spreadsheet.Font(),
                new DocumentFormat.OpenXml.Spreadsheet.Font(
                    new Bold(),
                    new DocumentFormat.OpenXml.Spreadsheet.Color { Rgb = "FFFFFFFF" }))
            { Count = 2 };
            var fills = new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(
                    new ForegroundColor { Rgb = "FF3C78C8" },
                    new BackgroundColor { Indexed = 64 })
                { PatternType = PatternValues.Solid })) { Count = 3 };
            var borders = new Borders(new Border()) { Count = 1 };
            var formats = new CellFormats(
                new CellFormat(),
                new CellFormat
                {
                    FontId = 1,
                    FillId = 2,
                    BorderId = 0,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyAlignment = true,
                    Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Center }
                },
                new CellFormat
                {
                    NumberFormatId = 164,
                    FontId = 0,
                    FillId = 0,
                    BorderId = 0,
                    ApplyNumberFormat = true
                }) { Count = 3 };
            return new Stylesheet(numberingFormats, fonts, fills, borders, formats);
        }

        private static bool TryReadWorksheet(
            WorksheetPart worksheetPart,
            IReadOnlyList<string> sharedStrings,
            CurveHistoryData data,
            out List<DateTime> times)
        {
            times = new List<DateTime>();
            var metadata = new Dictionary<uint, Dictionary<int, string>>();
            Dictionary<int, CurveHistorySeries>? worksheetSeries = null;

            using OpenXmlReader reader = OpenXmlReader.Create(worksheetPart);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                    continue;

                if (reader.LoadCurrentElement() is not Row row)
                    continue;
                uint rowIndex = row.RowIndex?.Value ?? 0;
                if (rowIndex is >= 1 and < ExcelDataStartRow)
                {
                    metadata[rowIndex] = row.Elements<Cell>().ToDictionary(
                        GetColumnIndex,
                        cell => ReadCellText(cell, sharedStrings));
                    continue;
                }
                if (rowIndex < ExcelDataStartRow)
                    continue;

                worksheetSeries ??= CreateWorksheetSeries(metadata);
                if (worksheetSeries.Count == 0)
                    return false;

                Dictionary<int, Cell> cells = row.Elements<Cell>().ToDictionary(GetColumnIndex);
                if (!cells.TryGetValue(1, out Cell? timeCell)
                    || !TryReadDateTime(timeCell, sharedStrings, out DateTime time))
                    continue;

                times.Add(time);
                foreach (var item in worksheetSeries)
                {
                    double value = cells.TryGetValue(item.Key, out Cell? valueCell)
                        && TryReadDouble(valueCell, sharedStrings, out double parsed)
                            ? parsed
                            : double.NaN;
                    item.Value.Values.Add(value);
                }
            }

            worksheetSeries ??= CreateWorksheetSeries(metadata);
            if (worksheetSeries.Count == 0 || times.Count == 0)
                return false;

            data.Series.AddRange(worksheetSeries.OrderBy(item => item.Key).Select(item => item.Value));
            return true;
        }

        private static Dictionary<int, CurveHistorySeries> CreateWorksheetSeries(
            IReadOnlyDictionary<uint, Dictionary<int, string>> metadata)
        {
            var result = new Dictionary<int, CurveHistorySeries>();
            if (!metadata.TryGetValue(2, out Dictionary<int, string>? keys))
                return result;

            foreach (var item in keys.Where(item => item.Key >= 2).OrderBy(item => item.Key))
            {
                if (!TryParseKey(item.Value.Trim(), out CurveSeriesKind kind, out ushort address, out int? bit))
                    continue;

                string header = GetMetadata(metadata, 1, item.Key).Trim();
                int nameStart = header.IndexOf(']');
                result[item.Key] = new CurveHistorySeries
                {
                    Key = item.Value.Trim(),
                    Kind = kind,
                    Address = address,
                    Bit = bit,
                    GroupName = GetMetadata(metadata, 3, item.Key),
                    Name = nameStart >= 0 ? header[(nameStart + 1)..].Trim() : header,
                    Unit = GetMetadata(metadata, 4, item.Key),
                    UseRightAxis = GetMetadata(metadata, 5, item.Key)
                        .Equals("R", StringComparison.OrdinalIgnoreCase),
                    ZeroText = GetMetadata(metadata, 6, item.Key),
                    OneText = GetMetadata(metadata, 7, item.Key)
                };
            }
            return result;
        }

        private static string GetMetadata(
            IReadOnlyDictionary<uint, Dictionary<int, string>> metadata,
            uint row,
            int column) =>
            metadata.TryGetValue(row, out Dictionary<int, string>? values)
            && values.TryGetValue(column, out string? value)
                ? value
                : "";

        private static string ReadCellText(Cell cell, IReadOnlyList<string> sharedStrings)
        {
            if (cell.DataType?.Value == CellValues.SharedString
                && int.TryParse(cell.CellValue?.Text, out int sharedIndex)
                && sharedIndex >= 0
                && sharedIndex < sharedStrings.Count)
                return sharedStrings[sharedIndex];
            return cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? cell.InnerText ?? "";
        }

        private static bool TryReadDouble(
            Cell cell,
            IReadOnlyList<string> sharedStrings,
            out double value) =>
            double.TryParse(ReadCellText(cell, sharedStrings), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);

        private static bool TryReadDateTime(
            Cell cell,
            IReadOnlyList<string> sharedStrings,
            out DateTime value)
        {
            string text = ReadCellText(cell, sharedStrings);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double oaDate))
            {
                value = DateTime.FromOADate(oaDate);
                return true;
            }
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                   || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
        }

        private static int GetColumnIndex(Cell cell)
        {
            string reference = cell.CellReference?.Value ?? "";
            int result = 0;
            foreach (char character in reference)
            {
                if (!char.IsLetter(character))
                    break;
                result = result * 26 + char.ToUpperInvariant(character) - 'A' + 1;
            }
            return result;
        }

        private static void WriteInlineStringCell(
            OpenXmlWriter writer,
            int column,
            uint row,
            string value,
            uint styleIndex)
        {
            writer.WriteElement(new Cell
            {
                CellReference = GetCellReference(column, row),
                DataType = CellValues.InlineString,
                StyleIndex = styleIndex,
                InlineString = new InlineString(
                    new Text(value ?? "") { Space = SpaceProcessingModeValues.Preserve })
            });
        }

        private static void WriteNumberCell(
            OpenXmlWriter writer,
            int column,
            uint row,
            double value,
            uint styleIndex)
        {
            writer.WriteElement(new Cell
            {
                CellReference = GetCellReference(column, row),
                DataType = CellValues.Number,
                StyleIndex = styleIndex,
                CellValue = new CellValue(value.ToString("R", CultureInfo.InvariantCulture))
            });
        }

        private static string GetCellReference(int column, uint row)
        {
            var letters = new StringBuilder();
            while (column > 0)
            {
                column--;
                letters.Insert(0, (char)('A' + column % 26));
                column /= 26;
            }
            return letters.Append(row.ToString(CultureInfo.InvariantCulture)).ToString();
        }

        private static string FormatAddress(CurveHistorySeries series) =>
            series.Bit.HasValue
                ? $"{series.Address} BIT{series.Bit.Value}"
                : series.Address.ToString(CultureInfo.InvariantCulture);

        private static void WriteJsonSeries(
            Utf8JsonWriter writer,
            CurveHistorySeries series,
            int sampleCount,
            Func<int, double> getValue)
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(CurveHistorySeries.Key), series.Key);
            writer.WriteNumber(nameof(CurveHistorySeries.Kind), (int)series.Kind);
            writer.WriteNumber(nameof(CurveHistorySeries.Address), series.Address);
            if (series.Bit.HasValue)
                writer.WriteNumber(nameof(CurveHistorySeries.Bit), series.Bit.Value);
            else
                writer.WriteNull(nameof(CurveHistorySeries.Bit));
            writer.WriteString(nameof(CurveHistorySeries.GroupName), series.GroupName);
            writer.WriteString(nameof(CurveHistorySeries.Name), series.Name);
            writer.WriteString(nameof(CurveHistorySeries.Unit), series.Unit);
            writer.WriteBoolean(nameof(CurveHistorySeries.UseRightAxis), series.UseRightAxis);
            writer.WriteString(nameof(CurveHistorySeries.ZeroText), series.ZeroText);
            writer.WriteString(nameof(CurveHistorySeries.OneText), series.OneText);
            writer.WriteStartArray(nameof(CurveHistorySeries.Values));
            for (int index = 0; index < sampleCount; index++)
                writer.WriteNumberValue(getValue(index));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        private static bool TryParseKey(
            string key,
            out CurveSeriesKind kind,
            out ushort address,
            out int? bit)
        {
            kind = CurveSeriesKind.Numeric;
            address = 0;
            bit = null;
            string[] parts = key.Split(':');
            if (parts.Length == 2 && parts[0] == "N" && ushort.TryParse(parts[1], out address))
                return true;
            if (parts.Length == 3 && parts[0] == "B"
                && ushort.TryParse(parts[1], out address)
                && int.TryParse(parts[2], out int bitNumber))
            {
                kind = CurveSeriesKind.Bit;
                bit = bitNumber;
                return bitNumber is >= 0 and <= 15;
            }
            return false;
        }

        private static void Validate(CurveHistoryData data)
        {
            if (data.Format != LogFormat || data.Version != 1)
                throw new InvalidDataException("曲线文件版本不受支持。");
            ValidateSeries(data.Times, data.Series, item => item.Values.Count == data.Times.Count);
        }

        private static void Validate(CompactCurveHistoryData data)
        {
            ValidateSeries(
                data.Times,
                data.Series,
                item => item.Kind == CurveSeriesKind.Numeric
                    ? item.Values.Count == data.Times.Count
                    : item.Bit is >= 0 and <= 15
                      && data.BitRegisters.TryGetValue(item.Address, out List<ushort>? values)
                      && values.Count == data.Times.Count);
        }

        private static void ValidateSeries(
            IReadOnlyCollection<DateTime> times,
            IEnumerable<CurveHistorySeries> source,
            Func<CurveHistorySeries, bool> hasCorrectLength)
        {
            List<CurveHistorySeries> series = source.ToList();
            if (times.Count == 0)
                throw new InvalidDataException("曲线文件中没有采样数据。");
            if (series.Count == 0)
                throw new InvalidDataException("曲线文件中没有曲线项目。");
            if (series.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != series.Count)
                throw new InvalidDataException("曲线文件中存在重复的曲线标识。");
            if (series.Any(item => !hasCorrectLength(item)))
                throw new InvalidDataException("曲线数据长度与时间点数量不一致。");
        }
    }
}
