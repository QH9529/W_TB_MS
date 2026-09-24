using System;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using W_TB_MS.Modbus;
using W_TB_MS.Models;

namespace W_TB_MS
{
    public partial class MainForm : Form
    {
        /// <summary>
        /// 同步切换两个分区（参数曲线/BIT曲线）的曲线选择树显示状态：
        /// 以参数树当前是否可见取反，二者统一显示或隐藏。返回切换后是否可见。
        /// </summary>
        internal static bool ApplyTreeSelectorToggle(TreeView first, TreeView second)
        {
            bool show = !first.Visible;
            first.Visible = show;
            second.Visible = show;
            return show;
        }

        private ComboBox CreateChartRangeSelector()
        {
            var selector = new ComboBox
            {
                Width = 78,
                Height = 28,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 1, 0, 0)
            };
            selector.Items.AddRange(ChartHistoryRangeNames.Cast<object>().ToArray());
            selector.SelectedIndex = Array.IndexOf(ChartHistoryRanges, _chartHistoryRange);
            selector.SelectedIndexChanged += ChartRangeSelector_SelectedIndexChanged;
            _chartRangeSelectors.Add(selector);
            return selector;
        }

        private void ChartRangeSelector_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_syncingChartRangeSelectors
                || sender is not ComboBox selector
                || selector.SelectedIndex < 0
                || selector.SelectedIndex >= ChartHistoryRanges.Length)
                return;

            _chartHistoryRange = ChartHistoryRanges[selector.SelectedIndex];
            _syncingChartRangeSelectors = true;
            try
            {
                foreach (ComboBox rangeSelector in _chartRangeSelectors)
                    rangeSelector.SelectedIndex = selector.SelectedIndex;
            }
            finally
            {
                _syncingChartRangeSelectors = false;
            }

            _followCurrentTime = true;
            RefreshAllChartsForSelectedRange();
        }

        private void RefreshAllChartsForSelectedRange()
        {
            try
            {
                _lastChartAutoScaleAt = DateTime.MinValue;
                AutoScaleChart();
                AutoScaleBitChart();
            }
            catch { }
        }

        private void BeginChartPointer(ScottPlot.WinForms.FormsPlot plot, Point location)
        {
            _chartPointerDownPlot = plot;
            _chartPointerDownLocation = location;
            _chartPointerDragDetected = false;
        }

        private void UpdateChartPointerDrag(
            ScottPlot.WinForms.FormsPlot plot,
            MouseEventArgs e,
            Action stopFollowing)
        {
            if (_chartPointerDownPlot != plot
                || _chartPointerDragDetected
                || e.Button == MouseButtons.None)
                return;

            int dx = e.X - _chartPointerDownLocation.X;
            int dy = e.Y - _chartPointerDownLocation.Y;
            if (dx * dx + dy * dy < 16)
                return;

            _chartPointerDragDetected = true;
            stopFollowing();
        }

        private void BeginChartInteraction()
        {
            _chartInteractionToken++;
        }

        private void EndChartInteraction(ScottPlot.WinForms.FormsPlot plot)
        {
            long token = _chartInteractionToken;
            if (!plot.IsHandleCreated || plot.IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    // 新一轮拖动已经开始时，旧事件不得释放新锁。
                    if (token != _chartInteractionToken || plot.IsDisposed)
                        return;
                    _chartPointerDownPlot = null;
                    _chartPointerDragDetected = false;
                    // 上下分区 X 轴联动：交互结束后以参数图为基准同步两图并统一重绘。
                    ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
                    if (plot == bitFormsPlot)
                    {
                        // 用户直接拖动/缩放下分区时，反向把 X 视口同步回参数图。
                        if (double.IsFinite(limits.Left) && double.IsFinite(limits.Right) && limits.Right > limits.Left)
                        {
                            _syncingChartXAxes = true;
                            try { formsPlot.Plot.Axes.SetLimitsX(limits.Left, limits.Right); }
                            finally { _syncingChartXAxes = false; }
                        }
                        UpdateChartRenderRange(formsPlot);
                    }
                    SyncBitChartXAxis(limits.Left, limits.Right);
                    UpdateChartRenderRange(formsPlot);
                    UpdateChartRenderRange(bitFormsPlot);
                    formsPlot.Refresh();
                    bitFormsPlot.Refresh();
                }));
            }
            catch (InvalidOperationException)
            {
                // 窗体关闭时可能无法再排队 UI 更新。
            }
        }

        private void RefreshVisibleChart(bool force = false)
        {
            try
            {
                if (!force && !_chartDataDirty)
                    return;
                long nowTick = Environment.TickCount64;
                if (!force && nowTick - _lastChartRenderTick < ChartRenderInterval.TotalMilliseconds)
                    return;
                _lastChartRenderTick = nowTick;
                bool shouldAutoScale = force
                    || DateTime.UtcNow - _lastChartAutoScaleAt >= ChartAutoScaleInterval;
                if (_followCurrentTime && shouldAutoScale)
                {
                    _lastChartAutoScaleAt = DateTime.UtcNow;
                    AutoScaleChart();
                    AutoScaleBitChart();
                }
                else
                {
                    // 用户拖动/缩放后保留当前视口，同时扩展可见数据范围并继续实时重绘。
                    RefreshChartPair();
                }
                _chartDataDirty = false;
            }
            catch { }
        }

        /// <summary>
        /// 上下分区 X 轴联动：以参数图当前 X 视口为准同步到 BIT 图后统一重绘。
        /// </summary>
        private void RefreshChartPair()
        {
            ScottPlot.AxisLimits limits = formsPlot.Plot.Axes.GetLimits();
            SyncBitChartXAxis(limits.Left, limits.Right);
            UpdateChartRenderRange(formsPlot);
            UpdateChartRenderRange(bitFormsPlot);
            formsPlot.Refresh();
            bitFormsPlot.Refresh();
        }

        /// <summary>
        /// 将 BIT 图 X 轴同步为指定范围（不触发反向同步）。
        /// </summary>
        private void SyncBitChartXAxis(double xMin, double xMax)
        {
            if (_syncingChartXAxes || !double.IsFinite(xMin) || !double.IsFinite(xMax) || xMax <= xMin)
                return;
            _syncingChartXAxes = true;
            try
            {
                bitFormsPlot.Plot.Axes.SetLimitsX(xMin, xMax);
            }
            finally
            {
                _syncingChartXAxes = false;
            }
        }

        private void ScheduleChartRenderRangeUpdate(ScottPlot.WinForms.FormsPlot plot)
        {
            if (!plot.IsHandleCreated || plot.IsDisposed)
                return;

            BeginInvoke(() =>
            {
                if (plot.IsDisposed)
                    return;
                UpdateChartRenderRange(plot);
                plot.Refresh();
            });
        }

        private void UpdateChartRenderRange(
            ScottPlot.WinForms.FormsPlot plot,
            double? visibleMinimum = null,
            double? visibleMaximum = null)
        {
            if (_timeData.Count == 0)
                return;

            ScottPlot.AxisLimits limits = plot.Plot.Axes.GetLimits();
            double xMinimum = visibleMinimum ?? limits.Left;
            double xMaximum = visibleMaximum ?? limits.Right;
            if (!double.IsFinite(xMinimum) || !double.IsFinite(xMaximum) || xMaximum <= xMinimum)
                return;

            int minimumIndex = Math.Max(0, FindFirstTimeIndexAtOrAfter(_timeData, xMinimum) - 1);
            int maximumIndex = FindFirstTimeIndexAtOrAfter(_timeData, xMaximum);
            if (maximumIndex < _timeData.Count && _timeData[maximumIndex] <= xMaximum)
                maximumIndex++;
            maximumIndex = Math.Clamp(maximumIndex, minimumIndex, _timeData.Count - 1);

            static void ApplyRange(ScottPlot.IPlottable curve, int minimum, int maximum)
            {
                if (curve is ScottPlot.Plottables.Scatter scatter)
                {
                    scatter.MinRenderIndex = minimum;
                    scatter.MaxRenderIndex = maximum;
                }
            }

            if (plot == formsPlot)
            {
                foreach (ScottPlot.IPlottable curve in _numericCurvePlottables.Values)
                    ApplyRange(curve, minimumIndex, maximumIndex);
                return;
            }

            foreach (BitCurveDefinition definition in BitCurveDefinitions)
            {
                if (_bitCurvePlottables.TryGetValue(
                    (definition.Address, definition.Bit),
                    out ScottPlot.IPlottable? curve))
                {
                    ApplyRange(curve, minimumIndex, maximumIndex);
                }
            }
        }

        private void PruneExpiredCurveData(DateTime now)
        {
            if (now < _nextCurvePruneAt)
                return;

            _nextCurvePruneAt = now + CurvePruneInterval;
            double cutoff = (now - CurveRetention).ToOADate();
            int removeCount = FindFirstTimeIndexAtOrAfter(_timeData, cutoff);
            if (removeCount <= 0)
                return;

            _timeData.RemoveRange(0, removeCount);
            foreach (List<double> numericData in _numericCurveData.Values)
                numericData.RemoveRange(0, removeCount);
            foreach (List<ushort> registerData in _allBitRegisterData.Values)
                registerData.RemoveRange(0, removeCount);

            UpdateChartRenderRange(formsPlot);
            UpdateChartRenderRange(bitFormsPlot);
        }

        private (double XMin, double XMax) ComputeXAxisLimits()
        {
            double now = DateTime.Now.ToOADate();
            // 当前时间固定在横轴正中间，历史数据位于左半区，右半区预留未来时间。
            double minimumHistorySpan = MinTimeWindowSeconds / 2.0 / 86400.0;
            double historySpan = _chartHistoryRange?.TotalDays
                ?? (_timeData.Count == 0 ? minimumHistorySpan : now - _timeData[0]);
            historySpan = Math.Max(historySpan, minimumHistorySpan);
            double margin = Math.Max(historySpan * XAxisMarginFraction, XAxisMinMarginSeconds / 86400.0);
            double halfWidth = historySpan + margin;
            return (now - halfWidth, now + halfWidth);
        }

        private void AutoScaleChart()
        {
            var (xMin, xMax) = ComputeXAxisLimits();
            UpdateChartRenderRange(formsPlot, xMin, xMax);
            formsPlot.Plot.Axes.AutoScale();
            NormalizePowerAxisWhenZero();
            if (xMax > xMin)
                formsPlot.Plot.Axes.SetLimitsX(xMin, xMax);
            formsPlot.Refresh();
        }

        private void NormalizePowerAxisWhenZero()
        {
            if (!_numericCurveData.TryGetValue(30233, out List<double>? powerData)
                || powerData.Count == 0)
                return;

            double minimum = double.PositiveInfinity;
            double maximum = double.NegativeInfinity;
            foreach (double value in powerData)
            {
                if (!double.IsFinite(value))
                    continue;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }

            if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
                return;

            // ScottPlot 对全 0 数据的自动范围会产生约 -40 的视觉下限，
            // 这里给功率右轴一个稳定范围，并让两个 Y 轴的 0 像素位置对齐。
            if (Math.Abs(maximum - minimum) < 1e-9)
            {
                double upper = Math.Max(1.0, Math.Abs(maximum) * 1.1);
                ScottPlot.AxisLimits primaryLimits = formsPlot.Plot.Axes.GetLimits();
                double primarySpan = primaryLimits.Bottom - primaryLimits.Top;
                double primaryZeroFraction = primarySpan > 0
                    ? (0 - primaryLimits.Top) / primarySpan
                    : 0;
                primaryZeroFraction = Math.Clamp(primaryZeroFraction, 0.0, 0.95);

                // 右轴上边界固定为一个小的正数，反推下边界，
                // 使右轴 0 与左轴 0 处于同一条水平线上。
                double lower = primaryZeroFraction >= 0.999
                    ? -upper
                    : -upper * primaryZeroFraction / Math.Max(0.05, 1.0 - primaryZeroFraction);
                formsPlot.Plot.Axes.SetLimitsY(lower, upper, formsPlot.Plot.Axes.Right);
            }
        }

        private void BackToNow()
        {
            _followCurrentTime = true;
            try
            {
                AutoScaleChart();
                AutoScaleBitChart();
            }
            catch { }
        }

        private CompactCurveHistoryData CreateCurveHistorySnapshot(int startIndex = 0, int? endExclusive = null)
        {
            int endIndex = Math.Clamp(endExclusive ?? _timeData.Count, 0, _timeData.Count);
            startIndex = Math.Clamp(startIndex, 0, endIndex);
            int sampleCount = endIndex - startIndex;
            var data = new CompactCurveHistoryData
            {
                ExportedAt = DateTime.Now,
                Times = _timeData
                    .GetRange(startIndex, sampleCount)
                    .Select(DateTime.FromOADate)
                    .ToList()
            };

            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                data.NumericSeries.Add(new CurveHistorySeries
                {
                    Key = $"N:{definition.Address}",
                    Kind = CurveSeriesKind.Numeric,
                    Address = definition.Address,
                    GroupName = definition.GroupName,
                    Name = definition.Name,
                    Unit = definition.Unit,
                    UseRightAxis = definition.UseRightAxis,
                    Values = _numericCurveData[definition.Address].GetRange(startIndex, sampleCount)
                });
            }

            foreach (BitCurveDefinition definition in BitCurveDefinitions)
            {
                data.BitSeries.Add(new CurveHistorySeries
                {
                    Key = $"B:{definition.Address}:{definition.Bit}",
                    Kind = CurveSeriesKind.Bit,
                    Address = definition.Address,
                    Bit = definition.Bit,
                    GroupName = definition.GroupName,
                    Name = definition.Name,
                    ZeroText = definition.ZeroText,
                    OneText = definition.OneText
                });
            }

            foreach (ushort address in BitCurveRegisterAddresses)
                data.BitRegisters[address] = _allBitRegisterData[address].GetRange(startIndex, sampleCount);

            return data;
        }

        private async Task TryAutomaticArchiveAsync(DateTime sampleTime)
        {
            if (_automaticArchiveInProgress
                || _closePromptInProgress
                || sampleTime < _nextAutomaticArchiveAt
                || sampleTime < _nextAutomaticArchiveRetryAt
                || _timeData.Count == 0)
                return;

            _automaticArchiveInProgress = true;
            DateTime segmentEnd = _nextAutomaticArchiveAt;
            try
            {
                int startIndex = FindFirstTimeIndexAtOrAfter(
                    _timeData,
                    _automaticArchiveSegmentStart.ToOADate());
                int endIndex = FindFirstTimeIndexAtOrAfter(_timeData, segmentEnd.ToOADate());
                if (endIndex > startIndex)
                {
                    CompactCurveHistoryData snapshot = CreateCurveHistorySnapshot(startIndex, endIndex);
                    (string logPath, string excelPath) = await SaveArchivePairAsync(snapshot);
                    AppendLog("SYS",
                        $"6小时曲线已自动存档: {logPath} | {excelPath}");
                }

                _automaticArchiveSegmentStart = segmentEnd;
                _nextAutomaticArchiveAt = segmentEnd + AutomaticArchiveInterval;
                _nextAutomaticArchiveRetryAt = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                _nextAutomaticArchiveRetryAt = DateTime.Now.AddMinutes(5);
                AppendLog("ERR",
                    $"曲线自动存档失败，5分钟后重试: {ex.Message}");
            }
            finally
            {
                _automaticArchiveInProgress = false;
            }
        }

        private async Task<(string LogPath, string ExcelPath)> SaveArchivePairAsync(
            CompactCurveHistoryData snapshot)
        {
            CurveArchivePair pair = await Task.Run(() =>
                CurveArchiveWriter.SavePair(_archiveFolder, snapshot));
            _lastSavedSampleTime = Math.Max(
                _lastSavedSampleTime,
                snapshot.Times[^1].ToOADate());
            return (pair.LogPath, pair.ExcelPath);
        }

        private async Task ExportCurveDataAsync(bool excel)
        {
            if (_timeData.Count == 0)
            {
                MessageBox.Show("当前还没有曲线采样数据。", "导出曲线", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = excel ? "xlsx" : "log",
                Filter = excel ? "Excel曲线文件 (*.xlsx)|*.xlsx" : "曲线LOG文件 (*.log)|*.log",
                FileName = $"热泵曲线_{DateTime.Now:yyyyMMdd_HHmmss}.{(excel ? "xlsx" : "log")}",
                Title = excel ? "导出Excel曲线" : "导出LOG曲线",
                InitialDirectory = _archiveFolder
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            CompactCurveHistoryData snapshot = CreateCurveHistorySnapshot();
            UseWaitCursor = true;
            try
            {
                if (excel)
                    await Task.Run(() => CurveHistoryFile.SaveExcel(dialog.FileName, snapshot));
                else
                    await Task.Run(() => CurveHistoryFile.SaveLog(dialog.FileName, snapshot));
                _lastSavedSampleTime = Math.Max(
                    _lastSavedSampleTime,
                    snapshot.Times[^1].ToOADate());
                MessageBox.Show($"曲线数据已导出：\n{dialog.FileName}", "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "导出曲线", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private async Task OpenCurveHistoryAsync()
        {
            using var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                Filter = "曲线文件 (*.log;*.xlsx)|*.log;*.xlsx|曲线LOG文件 (*.log)|*.log|Excel曲线文件 (*.xlsx)|*.xlsx",
                Title = "打开历史曲线文件",
                InitialDirectory = _archiveFolder
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            UseWaitCursor = true;
            try
            {
                CurveHistoryData data = await Task.Run(() => CurveHistoryFile.Load(dialog.FileName));
                var historyForm = new HistoryChartForm(data, dialog.FileName);
                historyForm.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取曲线文件失败：{ex.Message}", "打开历史曲线", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
            }
        }

        private void SetNumericCurveVisibility(NumericCurveDefinition definition, bool visible)
        {
            if (_numericCurvePlottables.TryGetValue(definition.Address, out ScottPlot.IPlottable? existingCurve))
            {
                existingCurve.IsVisible = visible;
                return;
            }
            if (!visible)
                return;

            var scatter = formsPlot.Plot.Add.Scatter(_timeData, _numericCurveData[definition.Address]);
            scatter.LegendText = definition.Name;
            scatter.MarkerSize = 0;
            if (definition.UseRightAxis)
                scatter.Axes.YAxis = formsPlot.Plot.Axes.Right;

            scatter.Color = definition.Address switch
            {
                30209 => ScottPlot.Colors.Blue,
                30210 => ScottPlot.Colors.Orange,
                30202 => ScottPlot.Colors.Red,
                30211 => ScottPlot.Colors.Green,
                30233 => ScottPlot.Colors.Purple,
                30221 => ScottPlot.Colors.Magenta,
                30234 => ScottPlot.Colors.Cyan,
                _ => scatter.Color
            };
            _numericCurvePlottables[definition.Address] = scatter;
            UpdateChartRenderRange(formsPlot);
        }

        private static double DecodeNumericCurveValue(
            NumericCurveDefinition definition,
            IReadOnlyDictionary<ushort, ushort> values)
        {
            ushort GetLatest(ushort address) =>
                values.TryGetValue(address, out ushort value) ? value : (ushort)0;

            ushort rawValue = GetLatest(definition.Address);
            return definition.ValueKind switch
            {
                NumericCurveValueKind.SignedTenths => (short)rawValue / 10.0,
                NumericCurveValueKind.UnsignedTenths => rawValue / 10.0,
                NumericCurveValueKind.UInt32HighLowTenths =>
                    (((uint)rawValue << 16) | GetLatest((ushort)(definition.Address + 1))) / 10.0,
                NumericCurveValueKind.UInt32LowHigh =>
                    ((uint)GetLatest((ushort)(definition.Address + 1)) << 16) | rawValue,
                _ => rawValue
            };
        }

        private static bool HasNumericCurveValue(
            NumericCurveDefinition definition,
            IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.ContainsKey(definition.Address))
                return false;
            return definition.ValueKind is not (NumericCurveValueKind.UInt32HighLowTenths
                or NumericCurveValueKind.UInt32LowHigh)
                || values.ContainsKey((ushort)(definition.Address + 1));
        }

        internal static List<double> DecodeBitValues(
            IReadOnlyList<ushort> rawValues,
            int bit,
            int startIndex = 0,
            int? count = null)
        {
            if (bit is < 0 or > 15)
                throw new ArgumentOutOfRangeException(nameof(bit));
            int valueCount = count ?? rawValues.Count - startIndex;
            if (startIndex < 0 || valueCount < 0 || startIndex > rawValues.Count - valueCount)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            var values = new List<double>(valueCount);
            int mask = 1 << bit;
            for (int index = startIndex; index < startIndex + valueCount; index++)
            {
                ushort rawValue = rawValues[index];
                values.Add((rawValue & mask) != 0 ? 1.0 : 0.0);
            }
            return values;
        }

        internal static double DecodeNumericCurveValue(
            ushort address,
            IReadOnlyDictionary<ushort, ushort> values)
        {
            NumericCurveDefinition definition = NumericCurveDefinitions.FirstOrDefault(item => item.Address == address)
                ?? throw new ArgumentOutOfRangeException(nameof(address), address, "不是数值曲线寄存器");
            return DecodeNumericCurveValue(definition, values);
        }

        internal static bool IsDurationAddress(ushort address) => address is 30235 or 30237;

        internal static string FormatDurationSeconds(double seconds)
        {
            if (!double.IsFinite(seconds))
                return "-- min -- s";

            // Duration registers are non-negative whole seconds. Clamp malformed
            // values so a transient invalid sample cannot produce a negative time.
            long totalSeconds = Math.Max(0L, (long)Math.Round(seconds, MidpointRounding.AwayFromZero));
            long minutes = totalSeconds / 60;
            long remainingSeconds = totalSeconds % 60;
            return $"{minutes} min {remainingSeconds:00} s";
        }

        private static string FormatNumericCurveValue(NumericCurveDefinition definition, double value)
        {
            if (IsDurationAddress(definition.Address))
                return FormatDurationSeconds(value);

            string format = definition.ValueKind is NumericCurveValueKind.SignedTenths
                or NumericCurveValueKind.UnsignedTenths
                or NumericCurveValueKind.UInt32HighLowTenths
                ? "F1"
                : "F0";
            return value.ToString(format, CultureInfo.CurrentCulture);
        }

        private void SetBitCurveVisibility(BitCurveDefinition definition, bool visible)
        {
            var key = (definition.Address, definition.Bit);
            if (_bitCurvePlottables.TryGetValue(key, out ScottPlot.IPlottable? existingCurve))
            {
                if (visible)
                    return;

                bitFormsPlot.Plot.Remove(existingCurve);
                _bitCurvePlottables.Remove(key);
                return;
            }
            if (!visible)
                return;

            ScottPlot.WinForms.FormsPlot targetPlot = bitFormsPlot;

            ScottPlot.IScatterSource source;
            if (definition.Address == RUNTIME_MODE_CURVE_ADDR)
            {
                // 运行模式1：枚举阶梯线 (0~7)
                source = new EnumScatterSource(
                    _timeData,
                    _allBitRegisterData[definition.Address],
                    RuntimeModeMeanings,
                    RUNTIME_MODE1_NONE);
            }
            else if (definition.Address == RUNTIME_MODE2_CURVE_ADDR)
            {
                // 运行模式2：枚举阶梯线 (0~6)
                source = new EnumScatterSource(
                    _timeData,
                    _allBitRegisterData[definition.Address],
                    RuntimeMode2Meanings,
                    RUNTIME_MODE2_COMPRESSOR_OFF);
            }
            else
            {
                // 其他状态曲线：按位画线
                source = new BitScatterSource(
                    _timeData,
                    _allBitRegisterData[definition.Address],
                    definition.Bit);
            }
            var scatter = targetPlot.Plot.Add.Scatter(source);
            scatter.LegendText = definition.Name;
            scatter.MarkerSize = 0;
            _bitCurvePlottables[key] = scatter;
            UpdateChartRenderRange(targetPlot);
        }

        private void AutoScaleBitChart()
        {
            var (xMin, xMax) = ComputeXAxisLimits();
            UpdateChartRenderRange(bitFormsPlot, xMin, xMax);
            bitFormsPlot.Plot.Axes.AutoScale();
            if (xMax > xMin)
                bitFormsPlot.Plot.Axes.SetLimitsX(xMin, xMax);
            // Y 轴范围按可见的运行模式1/2枚举曲线量程取最大；无枚举曲线时用 0/1
            double enumMaxY = -1;
            if (_bitCurvePlottables.TryGetValue((RUNTIME_MODE_CURVE_ADDR, 0), out var runtimeCurve1)
                && runtimeCurve1.IsVisible)
                enumMaxY = Math.Max(enumMaxY, RUNTIME_MODE1_NONE);
            if (_bitCurvePlottables.TryGetValue((RUNTIME_MODE2_CURVE_ADDR, 0), out var runtimeCurve2)
                && runtimeCurve2.IsVisible)
                enumMaxY = Math.Max(enumMaxY, RUNTIME_MODE2_COMPRESSOR_OFF);
            if (enumMaxY >= 0)
                bitFormsPlot.Plot.Axes.SetLimitsY(-0.5, enumMaxY + 0.5);
            else
                bitFormsPlot.Plot.Axes.SetLimitsY(-0.15, 1.15);
            bitFormsPlot.Refresh();
        }

        private void FormsPlot_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_timeData.Count == 0)
            {
                HideChartToolTip(formsPlot);
                return;
            }

            // 所有数值曲线共用 _timeData，按鼠标 X 定位时间索引后逐条列出可见曲线的值。
            ScottPlot.Coordinates mouseCoordinates = formsPlot.Plot.GetCoordinates(e.X, e.Y);
            int index = FindNearestTimeIndex(_timeData, mouseCoordinates.X);
            if (index < 0)
            {
                HideChartToolTip(formsPlot);
                return;
            }

            var lines = new List<string>();
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (!_numericCurvePlottables.TryGetValue(definition.Address, out ScottPlot.IPlottable? curve)
                    || !curve.IsVisible)
                    continue;
                IReadOnlyList<double> values = _numericCurveData[definition.Address];
                if (index >= values.Count)
                    continue;

                string value = FormatNumericCurveValue(definition, values[index]);
                string unitSuffix = IsDurationAddress(definition.Address)
                    ? string.Empty
                    : $" {definition.Unit}";
                lines.Add($"{definition.Name}：{value}{unitSuffix}");
            }

            if (lines.Count == 0)
            {
                HideChartToolTip(formsPlot);
                return;
            }

            string time = DateTime.FromOADate(_timeData[index]).ToString("HH:mm:ss.fff");
            ShowCurveToolTip(formsPlot, e, $"时间：{time}\n" + string.Join("\n", lines));
        }

        private void BitFormsPlot_MouseMove(object? sender, MouseEventArgs e)
        {
            if (sender is not ScottPlot.WinForms.FormsPlot plot)
                return;

            if (_timeData.Count == 0)
            {
                HideChartToolTip(plot);
                return;
            }

            // 下分区（状态+故障BIT曲线共用一个图）：列出所有勾选曲线的值与时间点。
            ScottPlot.Coordinates mouseCoordinates = plot.Plot.GetCoordinates(e.X, e.Y);
            int index = FindNearestTimeIndex(_timeData, mouseCoordinates.X);
            if (index < 0)
            {
                HideChartToolTip(plot);
                return;
            }

            var lines = new List<string>();
            foreach (BitCurveDefinition definition in BitCurveDefinitions)
            {
                var key = (definition.Address, definition.Bit);
                if (!_bitCurvePlottables.TryGetValue(key, out ScottPlot.IPlottable? curve)
                    || !curve.IsVisible)
                    continue;
                IReadOnlyList<ushort> rawValues = _allBitRegisterData[definition.Address];
                if (index >= rawValues.Count)
                    continue;

                ushort rawValue = rawValues[index];
                string time = DateTime.FromOADate(_timeData[index]).ToString("HH:mm:ss.fff");
                if (lines.Count == 0)
                    lines.Add($"时间：{time}");
                // 运行模式1/2虚拟曲线（枚举阶梯线）显示枚举文本；其余状态曲线按位解读
                if (definition.Address is RUNTIME_MODE_CURVE_ADDR or RUNTIME_MODE2_CURVE_ADDR
                    && rawValue is >= 0 and <= 7)
                {
                    var meanings = definition.Address == RUNTIME_MODE_CURVE_ADDR
                        ? RuntimeModeMeanings
                        : RuntimeMode2Meanings;
                    string modeName = definition.Address == RUNTIME_MODE_CURVE_ADDR
                        ? "运行模式1"
                        : "运行模式2";
                    lines.Add($"{modeName}：{rawValue} / {(meanings.TryGetValue(rawValue, out var m) ? m : rawValue.ToString())}");
                }
                else
                {
                    int value = (rawValue & (1 << definition.Bit)) != 0 ? 1 : 0;
                    string stateText = value == 1 ? definition.OneText : definition.ZeroText;
                    lines.Add($"{definition.Name}：{value} / {stateText}");
                }
            }

            if (lines.Count <= 1)
            {
                HideChartToolTip(plot);
                return;
            }
            ShowCurveToolTip(plot, e, string.Join("\n", lines));
        }

        /// <summary>
        /// 隐藏曲线 tooltip 并清空去重缓存：Hide 后若不清缓存，
        /// 同一时间点再次悬停会因文本未变被去重早退，tooltip 不再显示。
        /// </summary>
        private void HideChartToolTip(Control chartControl) => _chartToolTip.Hide(chartControl);

        private void ShowCurveToolTip(Control chartControl, MouseEventArgs e, string text) =>
            _chartToolTip.Show(chartControl, e, text);

        internal static int FindNearestTimeIndex(IReadOnlyList<double> times, double target)
        {
            if (times.Count == 0)
                return -1;

            int low = 0;
            int high = times.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (times[middle] < target)
                    low = middle + 1;
                else if (times[middle] > target)
                    high = middle - 1;
                else
                    return middle;
            }

            if (low <= 0)
                return 0;
            if (low >= times.Count)
                return times.Count - 1;
            return target - times[low - 1] <= times[low] - target ? low - 1 : low;
        }

        internal static int FindFirstTimeIndexAtOrAfter(IReadOnlyList<double> times, double target)
        {
            int low = 0;
            int high = times.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (times[middle] < target)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }
}
