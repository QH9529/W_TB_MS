using System.Drawing;
using System.Globalization;
using W_TB_MS.Models;

namespace W_TB_MS
{
    public sealed class HistoryChartForm : Form
    {
        private readonly CurveHistoryData _data;
        private readonly List<double> _timeData;
        private readonly ChartToolTip _chartToolTip = new();

        public HistoryChartForm(CurveHistoryData data, string sourcePath)
        {
            _data = data;
            _timeData = data.Times.Select(time => time.ToOADate()).ToList();

            Text = $"历史曲线 - {Path.GetFileName(sourcePath)}";
            Size = new Size(1200, 760);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterParent;

            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9F),
                Padding = new Point(14, 4)
            };
            tabs.TabPages.Add(CreateChartPage("数值曲线", CurveSeriesKind.Numeric));
            tabs.TabPages.Add(CreateChartPage("BIT状态", CurveSeriesKind.Bit));
            Controls.Add(tabs);
        }

        private TabPage CreateChartPage(string title, CurveSeriesKind kind)
        {
            var page = new TabPage(title) { Padding = new Padding(0) };
            var formsPlot = new ScottPlot.WinForms.FormsPlot { Dock = DockStyle.Fill };
            var chart = formsPlot.Plot;
            var tickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic
            {
                LabelFormatter = dateTime => dateTime.ToString("HH:mm:ss")
            };
            chart.Axes.Bottom.TickGenerator = tickGenerator;
            chart.Legend.IsVisible = false;

            List<CurveHistorySeries> series = _data.Series
                .Where(item => item.Kind == kind)
                .ToList();
            var plottables = new Dictionary<string, ScottPlot.IPlottable>(StringComparer.Ordinal);

            void SetVisibility(CurveHistorySeries item, bool visible)
            {
                if (plottables.TryGetValue(item.Key, out ScottPlot.IPlottable? existing))
                {
                    existing.IsVisible = visible;
                    return;
                }
                if (!visible)
                    return;

                var scatter = chart.Add.Scatter(_timeData, item.Values);
                scatter.LegendText = item.Name;
                if (item.UseRightAxis)
                    scatter.Axes.YAxis = chart.Axes.Right;
                plottables[item.Key] = scatter;
            }

            void ShowAllData()
            {
                chart.Axes.AutoScale();
                if (_timeData.Count == 1)
                {
                    double margin = 5.0 / 86400.0;
                    chart.Axes.SetLimitsX(_timeData[0] - margin, _timeData[0] + margin);
                }
                else if (_timeData.Count > 1)
                {
                    chart.Axes.SetLimitsX(_timeData.Min(), _timeData.Max());
                }
                if (kind == CurveSeriesKind.Bit)
                    chart.Axes.SetLimitsY(-0.15, 1.15);
                formsPlot.Refresh();
            }

            var selector = new TreeView
            {
                Dock = DockStyle.Right,
                Width = 280,
                CheckBoxes = true,
                HideSelection = false,
                ShowLines = true,
                ShowPlusMinus = true,
                Font = new Font("Microsoft YaHei", 8.5F)
            };
            foreach (var group in series.GroupBy(item => item.GroupName))
            {
                var groupNode = new TreeNode(group.Key);
                foreach (CurveHistorySeries item in group)
                    groupNode.Nodes.Add(new TreeNode(item.Name) { Tag = item });
                selector.Nodes.Add(groupNode);
            }
            foreach (TreeNode node in selector.Nodes)
                node.Expand();

            bool updatingChecks = false;
            selector.AfterCheck += (sender, args) =>
            {
                if (updatingChecks || args.Node is not TreeNode checkedNode)
                    return;

                if (checkedNode.Tag is CurveHistorySeries item)
                {
                    SetVisibility(item, checkedNode.Checked);
                }
                else
                {
                    updatingChecks = true;
                    foreach (TreeNode child in checkedNode.Nodes)
                    {
                        child.Checked = checkedNode.Checked;
                        if (child.Tag is CurveHistorySeries childItem)
                            SetVisibility(childItem, child.Checked);
                    }
                    updatingChecks = false;
                }
                ShowAllData();
            };

            var btnShowAll = new Button
            {
                Text = "显示全部",
                FlatStyle = FlatStyle.Flat,
                Location = new Point(10, 10),
                Size = new Size(90, 30),
                Font = new Font("Microsoft YaHei", 8.5F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnShowAll.Click += (sender, args) => ShowAllData();

            formsPlot.MouseMove += (sender, args) =>
                ShowHoverText(formsPlot, args, series, plottables);
            formsPlot.MouseLeave += (sender, args) => _chartToolTip.Hide(formsPlot);

            page.Controls.Add(formsPlot);
            page.Controls.Add(selector);
            page.Controls.Add(btnShowAll);
            btnShowAll.BringToFront();
            ShowAllData();
            return page;
        }

        private void ShowHoverText(
            ScottPlot.WinForms.FormsPlot formsPlot,
            MouseEventArgs mouseEvent,
            IReadOnlyList<CurveHistorySeries> series,
            IReadOnlyDictionary<string, ScottPlot.IPlottable> plottables)
        {
            if (_timeData.Count == 0)
                return;

            // 所有曲线共用同一时间轴，按鼠标 X 定位时间索引后逐条列出可见曲线的值。
            ScottPlot.Coordinates mouseCoordinates = formsPlot.Plot.GetCoordinates(mouseEvent.X, mouseEvent.Y);
            int index = MainForm.FindNearestTimeIndex(_timeData, mouseCoordinates.X);
            if (index < 0)
            {
                _chartToolTip.Hide(formsPlot);
                return;
            }

            var lines = new List<string>();
            foreach (CurveHistorySeries item in series)
            {
                if (!plottables.TryGetValue(item.Key, out ScottPlot.IPlottable? plot)
                    || !plot.IsVisible)
                    continue;
                if (index >= item.Values.Count)
                    continue;

                double value = item.Values[index];
                string valueText;
                string unitSuffix;
                if (item.Kind == CurveSeriesKind.Bit)
                {
                    int bitValue = value >= 0.5 ? 1 : 0;
                    valueText = $"{bitValue} / {(bitValue == 1 ? item.OneText : item.ZeroText)}";
                    unitSuffix = $" {item.Unit}";
                }
                else
                {
                    valueText = MainForm.IsDurationAddress(item.Address)
                        ? MainForm.FormatDurationSeconds(value)
                        : value.ToString("0.###", CultureInfo.CurrentCulture);
                    unitSuffix = MainForm.IsDurationAddress(item.Address)
                        ? string.Empty
                        : $" {item.Unit}";
                }
                lines.Add($"{item.Name}：{valueText}{unitSuffix}".TrimEnd());
            }

            if (lines.Count == 0)
            {
                _chartToolTip.Hide(formsPlot);
                return;
            }

            string text = $"时间：{_data.Times[index]:yyyy-MM-dd HH:mm:ss.fff}\n" +
                string.Join("\n", lines);
            _chartToolTip.Show(formsPlot, mouseEvent, text);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _chartToolTip.Dispose();
            base.OnFormClosed(e);
        }
    }
}
