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
    public class MainForm : Form
    {
        private readonly ModbusRtuClient _modbus = new();
        private System.Windows.Forms.Timer? _pollTimer;
        private bool _isPolling;
        private readonly Dictionary<ushort, int> _rowMap = new(); // 寄存器地址 -> 行索引
        private readonly Dictionary<(ushort Address, int Bit), int> _bitRowMap = new();
        private readonly HashSet<int> _groupRows = new(); // 分组标题行索引
        private readonly Dictionary<int, bool> _groupCollapsed = new(); // 分组是否折叠
        private bool _writeInProgress;

        // --- 实时曲线 ---
        private const int GridColumnsTotalWidth = 434;   // 参数、数值、单位、地址、属性、操作列总宽度
        private const int GridColumnsCompactWidth = 326; // 隐藏地址、属性后的可见列总宽度
        private const int GridChromeWidth = 22;          // 垂直滚动条 + 边框余量
        private const int LeftPanelTargetWidth = GridColumnsTotalWidth + GridChromeWidth;
        private const int LeftPanelCompactWidth = GridColumnsCompactWidth + GridChromeWidth;
        private const double XAxisMarginFraction = 0.05;      // 数据跨度5%两侧边距
        private const double XAxisMinMarginSeconds = 1.0;     // 边距下限
        private const double MinTimeWindowSeconds = 10.0;     // 单点/跨度不足时的中心窗口宽度
        private const float CurveClickDistancePixels = 10;
        private static readonly TimeSpan ChartRenderInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ChartAutoScaleInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan CurveRetention = TimeSpan.FromHours(24);
        private static readonly TimeSpan CurvePruneInterval = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan AutomaticArchiveInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan?[] ChartHistoryRanges =
        {
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(12),
            null
        };
        private static readonly string[] ChartHistoryRangeNames =
        {
            "10分钟", "1小时", "6小时", "12小时", "全部"
        };
        private ScottPlot.WinForms.FormsPlot formsPlot = null!;
        private ScottPlot.WinForms.FormsPlot bitFormsPlot = null!;
        private ScottPlot.WinForms.FormsPlot stateFormsPlot = null!;
        private TabControl chartTabs = null!;
        private TreeView numericSelector = null!;
        private TreeView bitSelector = null!;
        private TreeView stateSelector = null!;
        private readonly Dictionary<ushort, ScottPlot.IPlottable> _numericCurvePlottables = new();
        private readonly Dictionary<ushort, List<double>> _numericCurveData = new();
        private readonly Dictionary<(ushort Address, int Bit), ScottPlot.IPlottable> _bitCurvePlottables = new();
        // 每次采样保存全部8个BIT寄存器的完整16位原始值，与曲线是否勾选无关。
        private readonly Dictionary<ushort, List<ushort>> _allBitRegisterData = new();
        private bool _followCurrentTime = true;
        private bool _followBitCurrentTime = true;
        private bool _followStateCurrentTime = true;
        private TimeSpan? _chartHistoryRange = ChartHistoryRanges[0];
        private DateTime _nextCurvePruneAt = DateTime.MinValue;
        private DateTime _automaticArchiveSegmentStart;
        private DateTime _nextAutomaticArchiveAt;
        private DateTime _nextAutomaticArchiveRetryAt = DateTime.MinValue;
        private long _lastChartRenderTick;
        private bool _chartDataDirty;
        private System.Windows.Forms.Timer? _chartRenderTimer;
        private DateTime _lastChartAutoScaleAt = DateTime.MinValue;
        // 交互期间用令牌协调鼠标事件，避免异步 MouseUp 清理新一轮操作。
        private long _chartInteractionToken;
        private ScottPlot.WinForms.FormsPlot? _chartPointerDownPlot;
        private Point _chartPointerDownLocation;
        private bool _chartPointerDragDetected;
        private double _lastSavedSampleTime = double.NegativeInfinity;
        private Task? _activeAutomaticArchiveTask;
        private bool _automaticArchiveInProgress;
        private bool _closeConfirmed;
        private bool _closePromptInProgress;
        private string _archiveFolder = "";
        private bool _syncingChartRangeSelectors;
        private readonly List<ComboBox> _chartRangeSelectors = new();
        private readonly List<double> _timeData = new();

        internal static TimeSpan CurveRetentionDuration => CurveRetention;
        internal static TimeSpan AutomaticArchiveIntervalDuration => AutomaticArchiveInterval;
        internal static string SoftwareVersion =>
            typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "1.0.5";


        // --- 串口配置 ---
        private ComboBox cmbPort = null!;
        private ComboBox cmbBaudRate = null!;
        private ComboBox cmbSlaveAddr = null!;
        private Button btnConnect = null!;
        private Button btnRefresh = null!;
        private NumericUpDown nudInterval = null!;
        private readonly ToolTip _toolTip = new();
        private readonly ToolTip _chartToolTip = new()
        {
            InitialDelay = 0,
            ReshowDelay = 0,
            AutoPopDelay = 30000,
            ShowAlways = true,
            UseAnimation = false,
            UseFading = false
        };

        // --- 状态 ---
        private Label lblStatus = null!;
        private Label lblConnIndicator = null!;
        private Label lblUpdateTime = null!;

        // --- 数据展示 ---
        private DataGridView dgvData = null!;
        private Label lblFaultCode = null!;
        private ListBox lstFaults = null!;
        private Label lblWorkMode = null!;
        private Label lblRuntimeMode = null!;
        private Label lblRuntimeMode2 = null!;
        private Label lblSetMode = null!;
        private string _lastFaultSignature = string.Empty;
        private int _lastFaultCount = -1;

        // --- 通信日志 ---
        private RichTextBox rtbLog = null!;
        private Panel panelLog = null!;
        private Button btnToggleLog = null!;
        private CheckBox chkVerboseLog = null!;
        private Button btnArchiveFolder = null!;
        private System.Windows.Forms.Timer? _logFlushTimer;
        private readonly ConcurrentQueue<string> _pendingLogLines = new();
        private bool _verboseCommunicationLog;

        private bool _logVisible = true;

        // --- 曲线回位 / 故障列表折叠 ---
        private Button btnBackToNow = null!;
        private Button btnBitBackToNow = null!;
        private Button btnStateBackToNow = null!;
        private GroupBox grpFaultList = null!;

        // --- Modbus 自动重连 ---
        private int _failCount = 0;
        private int _pollInProgress;
        private int _pollGeneration;
        private string? _lastPortName = null;
        private const int ReconnectThreshold = 3;
        private const int ReconnectBaseDelayMs = 1000;   // 重连退避基数：1s 起步
        private const int ReconnectMaxDelayMs = 30000;   // 退避封顶：30s
        private int _reconnectActive;                    // 1 = 后台持续重连进行中
        private CancellationTokenSource? _reconnectCts;
        private static readonly (byte FunctionCode, ushort StartAddress, ushort Quantity)[] PollBlocks =
        {
            (0x04, 30001, 12),
            (0x04, 30101, 10),
            (0x04, 30201, 42),
            (0x03, 40001, 12),
            (0x03, 40201, 39), (0x03, 40301, 24)
        };

        // 某些旧设备拒绝读取较大的连续区间时，回退到原始分块。
        private static readonly (byte FunctionCode, ushort StartAddress, ushort Quantity)[] LegacyPollBlocks =
        {
            (0x04, 30001, 4), (0x04, 30005, 4), (0x04, 30009, 4),
            (0x04, 30101, 6), (0x04, 30201, 26), (0x04, 30226, 3),
            (0x04, 30107, 4), (0x04, 30229, 1), (0x04, 30231, 12),
            (0x03, 40001, 12), (0x03, 40201, 39), (0x03, 40301, 24)
        };

        private sealed record WriteRule(
            decimal Minimum,
            decimal Maximum,
            decimal Scale = 1,
            bool Signed = false,
            bool AllowFFFF = false);

        private sealed record BitCurveDefinition(
            ushort Address,
            int Bit,
            string GroupName,
            string Name,
            string ZeroText,
            string OneText,
            bool SelectedByDefault = false);

        private enum NumericCurveValueKind
        {
            Unsigned,
            SignedTenths,
            UnsignedTenths,
            UInt32HighLowTenths,
            UInt32LowHigh
        }

        private sealed record NumericCurveDefinition(
            ushort Address,
            string GroupName,
            string Name,
            string Unit,
            NumericCurveValueKind ValueKind,
            bool SelectedByDefault = false,
            bool UseRightAxis = false);

        // === 枚举阶梯线数据源（用于运行模式 0~5 阶梯图）===
        internal sealed class EnumScatterSource : ScottPlot.IScatterSource
        {
            private readonly List<double> _times;
            private readonly List<ushort> _values;
            private readonly IReadOnlyList<ScottPlot.Coordinates> _points;
            private int _minRenderIndex;
            private int _maxRenderIndex = int.MaxValue;
            private readonly Dictionary<ushort, string> _meanings;
            private readonly double _maxY;

            internal EnumScatterSource(List<double> times, List<ushort> values, Dictionary<ushort, string> meanings, double maxY)
            {
                _times = times;
                _values = values;
                _meanings = meanings;
                _maxY = maxY;
                _points = new CoordinatesView(this);
            }

            public IReadOnlyList<ScottPlot.Coordinates> GetScatterPoints() => _points;

            public ScottPlot.DataPoint GetNearest(
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                int index = FindNearestIndex(mouseCoordinates.X);
                return IsWithinDistance(index, mouseCoordinates, renderDetails, maxDistance)
                    ? new ScottPlot.DataPoint(GetPoint(index), index)
                    : ScottPlot.DataPoint.None;
            }

            public ScottPlot.DataPoint GetNearestX(
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                int index = FindNearestIndex(mouseCoordinates.X);
                if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX))
                    return ScottPlot.DataPoint.None;

                double distance = Math.Abs((_times[index] - mouseCoordinates.X) * renderDetails.PxPerUnitX);
                return distance <= maxDistance
                    ? new ScottPlot.DataPoint(GetPoint(index), index)
                    : ScottPlot.DataPoint.None;
            }

            public ScottPlot.CoordinateRange GetLimitsX() =>
                _times.Count == 0
                    ? new ScottPlot.CoordinateRange(0, 1)
                    : new ScottPlot.CoordinateRange(_times[0], _times[^1]);

            public ScottPlot.CoordinateRange GetLimitsY() =>
                new ScottPlot.CoordinateRange(-0.5, _maxY + 0.5);

            public ScottPlot.AxisLimits GetLimits() =>
                new(GetLimitsX().Min, GetLimitsX().Max, -0.5, _maxY + 0.5);

            public int MinRenderIndex
            {
                get => _minRenderIndex;
                set => _minRenderIndex = Math.Max(0, value);
            }

            public int MaxRenderIndex
            {
                get => _maxRenderIndex;
                set => _maxRenderIndex = value;
            }

            private ScottPlot.Coordinates GetPoint(int index) =>
                new(_times[index], _values[index]);

            internal int FindNearestIndex(double x)
            {
                if (_times.Count == 0 || _values.Count == 0)
                    return -1;

                int low = Math.Max(0, _minRenderIndex);
                int high = Math.Min(Math.Min(_times.Count, _values.Count) - 1, _maxRenderIndex);
                if (high < low)
                    return -1;
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    if (_times[middle] < x) low = middle + 1;
                    else if (_times[middle] > x) high = middle - 1;
                    else return middle;
                }
                if (low > high)
                {
                    // Clamp 的 min>max 会抛异常：x 越出窗口时夹回窗口边界点
                    int min = Math.Max(0, _minRenderIndex);
                    return high < min ? min : Math.Clamp(low, min, high);
                }
                return low;
            }

            private bool IsWithinDistance(
                int index,
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX)
                    || !double.IsFinite(renderDetails.PxPerUnitY))
                    return false;
                ScottPlot.Coordinates point = GetPoint(index);
                double dx = (point.X - mouseCoordinates.X) * renderDetails.PxPerUnitX;
                double dy = (point.Y - mouseCoordinates.Y) * renderDetails.PxPerUnitY;
                return Math.Sqrt(dx * dx + dy * dy) <= maxDistance;
            }

            private sealed class CoordinatesView : IReadOnlyList<ScottPlot.Coordinates>
            {
                private readonly EnumScatterSource _source;
                internal CoordinatesView(EnumScatterSource source) => _source = source;
                public int Count => Math.Min(_source._times.Count, _source._values.Count);
                public ScottPlot.Coordinates this[int index] => _source.GetPoint(index);
                public IEnumerator<ScottPlot.Coordinates> GetEnumerator()
                {
                    for (int i = 0; i < Count; i++) yield return _source.GetPoint(i);
                }
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
            }

            internal string GetMeaning(ushort val) =>
                _meanings.TryGetValue(val, out var m) ? m : val.ToString();
        }

        private sealed class BitScatterSource : ScottPlot.IScatterSource
        {
            private readonly List<double> _times;
            private readonly List<ushort> _registerValues;
            private readonly int _bit;
            private readonly IReadOnlyList<ScottPlot.Coordinates> _points;
            private int _minRenderIndex;
            private int _maxRenderIndex = int.MaxValue;

            internal BitScatterSource(List<double> times, List<ushort> registerValues, int bit)
            {
                _times = times;
                _registerValues = registerValues;
                _bit = bit;
                _points = new CoordinatesView(this);
            }

            public IReadOnlyList<ScottPlot.Coordinates> GetScatterPoints() => _points;

            public ScottPlot.DataPoint GetNearest(
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                int index = FindNearestIndex(mouseCoordinates.X);
                return IsWithinDistance(index, mouseCoordinates, renderDetails, maxDistance)
                    ? new ScottPlot.DataPoint(GetPoint(index), index)
                    : ScottPlot.DataPoint.None;
            }

            public ScottPlot.DataPoint GetNearestX(
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                int index = FindNearestIndex(mouseCoordinates.X);
                if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX))
                    return ScottPlot.DataPoint.None;

                double distance = Math.Abs((_times[index] - mouseCoordinates.X) * renderDetails.PxPerUnitX);
                return distance <= maxDistance
                    ? new ScottPlot.DataPoint(GetPoint(index), index)
                    : ScottPlot.DataPoint.None;
            }

            public ScottPlot.CoordinateRange GetLimitsX() =>
                _times.Count == 0
                    ? new ScottPlot.CoordinateRange(0, 1)
                    : new ScottPlot.CoordinateRange(_times[0], _times[^1]);

            public ScottPlot.CoordinateRange GetLimitsY() =>
                new ScottPlot.CoordinateRange(0, 1);

            public ScottPlot.AxisLimits GetLimits() =>
                new(GetLimitsX().Min, GetLimitsX().Max, 0, 1);

            public int MinRenderIndex
            {
                get => _minRenderIndex;
                set => _minRenderIndex = Math.Max(0, value);
            }

            public int MaxRenderIndex
            {
                get => _maxRenderIndex;
                set => _maxRenderIndex = value;
            }

            private ScottPlot.Coordinates GetPoint(int index) =>
                new(_times[index], (_registerValues[index] & (1 << _bit)) == 0 ? 0.0 : 1.0);

            private int FindNearestIndex(double x)
            {
                if (_times.Count == 0 || _registerValues.Count == 0)
                    return -1;

                int low = Math.Max(0, _minRenderIndex);
                int high = Math.Min(Math.Min(_times.Count, _registerValues.Count) - 1, _maxRenderIndex);
                if (high < low)
                    return -1;
                while (low <= high)
                {
                    int middle = low + (high - low) / 2;
                    if (_times[middle] < x) low = middle + 1;
                    else if (_times[middle] > x) high = middle - 1;
                    else return middle;
                }
                if (low > high)
                {
                    // Clamp 的 min>max 会抛异常：x 越出窗口时夹回窗口边界点
                    int min = Math.Max(0, _minRenderIndex);
                    return high < min ? min : Math.Clamp(low, min, high);
                }
                return low;
            }

            private bool IsWithinDistance(
                int index,
                ScottPlot.Coordinates mouseCoordinates,
                ScottPlot.RenderDetails renderDetails,
                float maxDistance)
            {
                if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX)
                    || !double.IsFinite(renderDetails.PxPerUnitY))
                    return false;
                ScottPlot.Coordinates point = GetPoint(index);
                double dx = (point.X - mouseCoordinates.X) * renderDetails.PxPerUnitX;
                double dy = (point.Y - mouseCoordinates.Y) * renderDetails.PxPerUnitY;
                return Math.Sqrt(dx * dx + dy * dy) <= maxDistance;
            }

            private sealed class CoordinatesView : IReadOnlyList<ScottPlot.Coordinates>
            {
                private readonly BitScatterSource _source;

                internal CoordinatesView(BitScatterSource source) => _source = source;

                public int Count => Math.Min(_source._times.Count, _source._registerValues.Count);

                public ScottPlot.Coordinates this[int index] => _source.GetPoint(index);

                public IEnumerator<ScottPlot.Coordinates> GetEnumerator()
                {
                    for (int index = 0; index < Count; index++)
                        yield return this[index];
                }

                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
                    GetEnumerator();
            }
        }

        private static readonly IReadOnlyList<NumericCurveDefinition> NumericCurveDefinitions = new NumericCurveDefinition[]
        {
            new(30108, "快速数据", "空调进水温度（快速）", "℃", NumericCurveValueKind.SignedTenths),
            new(30109, "快速数据", "压缩机频率（快速）", "Hz", NumericCurveValueKind.Unsigned),
            new(30110, "快速数据", "4G写保持寄存器计数", "次", NumericCurveValueKind.Unsigned),

            new(30202, "温度", "排气温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30203, "温度", "热回收出口温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30204, "温度", "中盘温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30205, "温度", "出盘温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30206, "温度", "经济器进口温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30207, "温度", "经济器出口温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30208, "温度", "吸气温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30209, "温度", "空调进水温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30210, "温度", "空调出水温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30211, "温度", "环境温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30212, "温度", "热水进水温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30213, "温度", "热水出水温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30214, "温度", "热水温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30226, "温度", "热水上温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30227, "温度", "热水中温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30228, "温度", "热水下温度", "℃", NumericCurveValueKind.SignedTenths),
            new(30242, "温度", "压缩机IPM模块温度", "℃", NumericCurveValueKind.SignedTenths),

            new(30215, "压力", "低压传感器压力", "bar", NumericCurveValueKind.UnsignedTenths),
            new(30216, "压力", "高压传感器压力", "bar", NumericCurveValueKind.UnsignedTenths),

            new(30221, "频率与转速", "压缩机频率", "Hz", NumericCurveValueKind.Unsigned),
            new(30222, "频率与转速", "上风机转速", "RPM", NumericCurveValueKind.Unsigned),
            new(30225, "频率与转速", "下风机转速", "RPM", NumericCurveValueKind.Unsigned),
            new(30234, "频率与转速", "压缩机目标频率", "Hz", NumericCurveValueKind.Unsigned),

            new(30219, "工作状态", "主路EXV开度", "步", NumericCurveValueKind.Unsigned),
            new(30220, "工作状态", "辅路EXV开度", "步", NumericCurveValueKind.Unsigned),
            new(30223, "执行器", "电动三通球阀1", "步", NumericCurveValueKind.Unsigned),
            new(30224, "执行器", "电动三通球阀2", "步", NumericCurveValueKind.Unsigned),

            new(30217, "电气与能耗", "累计耗电量", "kWh", NumericCurveValueKind.UInt32HighLowTenths),
            new(30231, "电气与能耗", "AC电压", "V", NumericCurveValueKind.Unsigned),
            new(30232, "电气与能耗", "AC电流", "A", NumericCurveValueKind.UnsignedTenths),
            new(30233, "电气与能耗", "当前功率", "W", NumericCurveValueKind.Unsigned, UseRightAxis: true),
            new(30239, "电气与能耗", "DC电压", "V", NumericCurveValueKind.Unsigned),
            new(30240, "电气与能耗", "压缩机电流", "A", NumericCurveValueKind.UnsignedTenths),
            new(30241, "电气与能耗", "DC风机电流", "A", NumericCurveValueKind.UnsignedTenths),

            new(30235, "工作状态", "压缩机运行时间", "s", NumericCurveValueKind.UInt32LowHigh),
            new(30237, "工作状态", "压缩机停止时间", "s", NumericCurveValueKind.UInt32LowHigh)
        };

        internal static IReadOnlyList<ushort> NumericCurveAddresses =>
            NumericCurveDefinitions.Select(item => item.Address).ToArray();

        private static readonly IReadOnlyList<BitCurveDefinition> BitCurveDefinitions = CreateBitCurveDefinitions();

        private static readonly IReadOnlyList<BitCurveDefinition> FaultCurveDefinitions =
            BitCurveDefinitions.Where(item => !IsStatusCurve(item)).ToArray();

        private static readonly IReadOnlyList<BitCurveDefinition> StatusCurveDefinitions =
            BitCurveDefinitions.Where(IsStatusCurve).ToArray();

        private static readonly IReadOnlyList<ushort> BitCurveRegisterAddresses =
            BitCurveDefinitions.Select(item => item.Address).Distinct().ToArray();

        private static readonly HashSet<ushort> CurveSampleAddresses = CreateCurveSampleAddresses();

        internal static IReadOnlyList<ushort> FaultCurveAddresses =>
            FaultCurveDefinitions.Select(item => item.Address).Distinct().ToArray();

        internal static IReadOnlyList<ushort> StatusCurveAddresses =>
            StatusCurveDefinitions.Select(item => item.Address).Distinct().ToArray();

        internal static int DefaultCurveSelectionCount =>
            NumericCurveDefinitions.Count(item => item.SelectedByDefault) +
            BitCurveDefinitions.Count(item => item.SelectedByDefault);

        internal static int StoredBitRegisterCount => BitCurveRegisterAddresses.Count;
        internal static int StoredBitCurveCount => BitCurveDefinitions.Count;

        /// <summary>
        /// 同步切换三个页面的曲线选择树显示状态：
        /// 以第一个树当前是否可见取反，三者统一显示或隐藏。返回切换后是否可见。
        /// </summary>
        internal static bool ApplyTreeSelectorToggle(TreeView first, TreeView second, TreeView third)
        {
            bool show = !first.Visible;
            first.Visible = show;
            second.Visible = show;
            third.Visible = show;
            return show;
        }

        private static IReadOnlyList<BitCurveDefinition> CreateBitCurveDefinitions()
        {
            var definitions = new List<BitCurveDefinition>();

            void AddFaultRegister(ushort address, string groupName, Dictionary<int, string> bits)
            {
                foreach (var item in bits.OrderBy(item => item.Key))
                    definitions.Add(new(address, item.Key, groupName, item.Value, "正常", "触发"));
            }

            void AddStatusRegister(
                ushort address,
                string groupName,
                Dictionary<int, BitDefinition> bits,
                params int[] defaults)
            {
                var defaultBits = defaults.ToHashSet();
                foreach (var item in bits.OrderBy(item => item.Key))
                {
                    definitions.Add(new(
                        address,
                        item.Key,
                        groupName,
                        item.Value.Name,
                        item.Value.ZeroText,
                        item.Value.OneText,
                        defaultBits.Contains(item.Key)));
                }
            }

            AddFaultRegister(30101, "故障报警一", RegisterMap.FaultReg1Bits);
            AddFaultRegister(30102, "故障报警二", RegisterMap.FaultReg2Bits);
            AddFaultRegister(30103, "故障报警三", RegisterMap.FaultReg3Bits);
            AddFaultRegister(30104, "故障报警四", RegisterMap.FaultReg4Bits);
            AddFaultRegister(30105, "故障报警五", RegisterMap.FaultReg5Bits);
            AddStatusRegister(30106, "主机运行状态", RegisterMap.StatusWordBits);
            AddStatusRegister(30201, "设备运行状态", RegisterMap.DeviceStatusBits);
            AddStatusRegister(30229, "拨码及阀门状态", RegisterMap.DipSwitchBits);
            // 40201/40202/40212 为枚举值寄存器，按位映射为状态曲线：
            // 40201: 0x0001=制冷(bit0)、0x0002=制热(bit1)；40202/40212: bit0。
            AddStatusRegister(
                40201,
                "热泵主机工作模式",
                new Dictionary<int, BitDefinition>
                {
                    [0] = new("工作模式-制冷", "未激活", "制冷"),
                    [1] = new("工作模式-制热", "未激活", "制热")
                });
            AddStatusRegister(
                40202,
                "生活热水功能",
                new Dictionary<int, BitDefinition>
                {
                    [0] = new("生活热水功能启用", "关闭", "打开")
                });
            AddStatusRegister(
                40212,
                "生活热水模式",
                new Dictionary<int, BitDefinition>
                {
                    [0] = new("生活热水模式-舒适", "节能", "舒适")
                });
            // 虚拟状态曲线（无真实寄存器，轮询时由 DeriveModeRegisters 派生）：
            // 30990 运行模式1（30106 bit3~5），30991 运行模式2（30106 bit3~5+bit6），状态页作单条阶梯线。
            // 用 bit=0 单条定义（不走 AddStatusRegister 的按位展开），文本含义分别取自 RuntimeMode(2)Meanings。
            definitions.Add(new(
                RUNTIME_MODE_CURVE_ADDR,
                0,
                "运行模式1",
                "运行模式1",
                "数据缺失",
                "运行中"));
            definitions.Add(new(
                RUNTIME_MODE2_CURVE_ADDR,
                0,
                "运行模式2",
                "运行模式2",
                "数据缺失",
                "运行中"));

            return definitions;
        }

        private static bool IsStatusCurve(BitCurveDefinition definition) =>
            definition.Address is 30106 or 30201 or 30229 or 40201 or 40202 or 40212
                or RUNTIME_MODE_CURVE_ADDR or RUNTIME_MODE2_CURVE_ADDR;

        private static HashSet<ushort> CreateCurveSampleAddresses()
        {
            // 虚拟模式寄存器（30990/30991）不参与设备轮询，采样地址集合需排除。
            var addresses = BitCurveRegisterAddresses
                .Where(address => address != RUNTIME_MODE_CURVE_ADDR && address != RUNTIME_MODE2_CURVE_ADDR)
                .ToHashSet();
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                addresses.Add(definition.Address);
                if (definition.ValueKind is NumericCurveValueKind.UInt32HighLowTenths
                    or NumericCurveValueKind.UInt32LowHigh)
                {
                    addresses.Add((ushort)(definition.Address + 1));
                }
            }
            return addresses;
        }

        internal static bool CanRecordCurveSample(IReadOnlyDictionary<ushort, ushort> values) =>
            CurveSampleAddresses.All(values.ContainsKey);

        internal static IReadOnlyCollection<ushort> RequiredCurveSampleAddresses => CurveSampleAddresses;

        private static readonly Dictionary<ushort, WriteRule> WriteRules = new()
        {
            [40001] = new(0, 1), [40002] = new(0, 2), [40003] = new(0, 1),
            [40004] = new(1, 50000, AllowFFFF: true), [40005] = new(0, 360, AllowFFFF: true),
            [40006] = new(0, 1), [40007] = new(0, 1), [40008] = new(0, 1),
            [40009] = new(0, 2), [40010] = new(0, 1), [40011] = new(0, 1),
            [40012] = new(0, 32),
            [40101] = new(0, 65535), [40102] = new(-100, 100, 10, true),
            [40103] = new(0, 1), [40105] = new(0, 1), [40106] = new(0, 3),
            [40111] = new(0, 100, 10),
            [40201] = new(1, 2), [40202] = new(0, 1), [40203] = new(0, 1),
            [40204] = new(10, 25, 10), [40205] = new(5, 20, 10),
            [40206] = new(20, 55, 10), [40207] = new(25, 60, 10),
            [40208] = new(0, 1), [40209] = new(10, 90), [40210] = new(1, 8, 10),
            [40211] = new(1, 8, 10), [40212] = new(0, 1),
            [40214] = new(40, 120), [40215] = new(3, 8, 10), [40216] = new(2, 5, 10),
            [40217] = new(-15, -2, 10, true), [40218] = new(-40, -15, 10, true),
            [40219] = new(5, 45, 10), [40220] = new(5, 15), [40221] = new(25, 90),
            [40222] = new(5, 20, 10), [40223] = new(0, 1),
            [40224] = new(3, 30), [40225] = new(2, 10),
            [40226] = new(0, 1), [40227] = new(0, 1),
            [40228] = new(0, 2), [40229] = new(0, 2), [40230] = new(0, 2),
            [40231] = new(1, 1000), [40232] = new(1, 1000), [40233] = new(1, 1000),
            [40235] = new(1, 1000), [40236] = new(1, 1000), [40237] = new(1, 1000),
            [40238] = new(1, 1000), [40239] = new(0, 600),
            [40301] = new(-5, 5, 10, true), [40302] = new(-5, 5, 10, true),
            [40303] = new(-5, 5, 10, true), [40304] = new(-5, 5, 10, true),
            [40305] = new(30, 140), [40306] = new(0, 80),
            [40307] = new(500, 1000), [40308] = new(3, 10, 10),
            [40309] = new(5, 40), [40310] = new(5, 50),
            [40311] = new(0, 140), [40312] = new(0, 140), [40313] = new(0, 140),
            [40314] = new(0, 140), [40315] = new(0, 140), [40316] = new(0, 140),
            [40317] = new(0, 140), [40318] = new(0, 140), [40319] = new(0, 140),
            [40320] = new(0, 140), [40321] = new(0, 3, 10),
            [40322] = new(30, 70, 10), [40323] = new(0, 3, 10),
            [40324] = new(30, 70, 10)
        };

        // Hold-register values with protocol-defined state meanings. Other
        // registers remain displayed using their numeric/scaled value.
        private static readonly Dictionary<ushort, Dictionary<ushort, string>> ParameterValueMeanings = new()
        {
            [40001] = new() { [0] = "关机", [1] = "开机" },
            [40002] = new() { [0] = "普通", [1] = "静音", [2] = "超级静音" },
            [40003] = new() { [0] = "关闭", [1] = "打开" },
            [40006] = new() { [0] = "无请求", [1] = "请求除霜" },
            [40007] = new() { [0] = "关闭", [1] = "运行" },
            [40008] = new() { [0] = "关闭", [1] = "运行" },
            [40009] = new() { [0] = "关闭", [1] = "打开" },
            [40010] = new() { [0] = "关闭", [1] = "打开" },
            [40011] = new() { [0] = "关闭", [1] = "强制待机" },
            [40103] = new() { [0] = "无请求", [1] = "请求除霜" },
            [40105] = new() { [0] = "关闭", [1] = "启用" },
            [40201] = new() { [1] = "制冷", [2] = "制热" },
            [40202] = new() { [0] = "禁用", [1] = "启用" },
            [40203] = new() { [0] = "不支持", [1] = "支持" },
            [40208] = new() { [0] = "进水温度", [1] = "出水温度" },
            [40212] = new() { [0] = "节能", [1] = "舒适" },
            [40223] = new() { [0] = "不交替", [1] = "交替" },
            [40226] = new() { [0] = "禁用", [1] = "启用" },
            [40227] = new() { [0] = "禁用", [1] = "启用" },
            [40228] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40229] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" },
            [40230] = new() { [0] = "关闭", [1] = "打开", [2] = "自动" }
        };

        public MainForm()
        {
            _archiveFolder = ArchiveSettingsStore.LoadArchiveFolder();
            InitializeComponent();
            _modbus.FrameObserved += AppendLog;
            // 每次启动，只要未设置存档路径就弹窗提醒并引导选择目录。
            // 弹窗放在 Shown 之后，确保主界面已显示、选择框不会停留在后台被遮住。
            Shown += (s, e) => BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && !Disposing && string.IsNullOrWhiteSpace(_archiveFolder))
                {
                    MessageBox.Show(
                        "尚未设置曲线存档路径，自动存档（LOG/Excel）将无法保存。\n请在下一步选择存档目录。",
                        "存档路径提醒",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    ChooseArchiveFolder();
                }
            }));
            Shown += (s, e) =>
            {
                WindowState = FormWindowState.Normal;
                Activate();
                BringToFront();
            };
            FormClosing += MainForm_FormClosing;
        }

        private void InitializeComponent()
        {
            // 标题栏统一显示软件名称和版本号，顶部工具栏不再重复显示版本。
            Text = $"W_TB_MS v{SoftwareVersion}";
            Size = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1000, 650);
            try { Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TB_MS.ico")); } catch { }

            // =============================================
            //  顶部：串口配置栏
            // =============================================
            var panelTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                Padding = new Padding(10),
                Font = new Font("Microsoft YaHei", 9F)
            };
            panelTop.Paint += (s, e) =>
            {
                using var separatorPen = new Pen(Color.Silver, 1F);
                int y = panelTop.ClientSize.Height - 1;
                e.Graphics.DrawLine(separatorPen, 0, y, panelTop.ClientSize.Width - 1, y);
            };

            var lblPort = new Label { Text = "串口:", Location = new Point(10, 20), AutoSize = true };
            cmbPort = new ComboBox { Location = new Point(55, 17), Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, ItemHeight = 25 };

            btnRefresh = new Button { Text = "刷新", Location = new Point(172, 16), Width = 60, Height = 28 };
            btnRefresh.Click += (s, e) => RefreshPorts();

            var lblBaud = new Label { Text = "波特率:", Location = new Point(248, 20), AutoSize = true };
            cmbBaudRate = new ComboBox { Location = new Point(303, 17), Width = 95, DropDownStyle = ComboBoxStyle.DropDownList, ItemHeight = 25 };
            cmbBaudRate.Items.AddRange(new object[] { "2400", "4800", "9600", "19200", "38400", "115200" });
            cmbBaudRate.SelectedIndex = 2;

            var lblSlave = new Label { Text = "从机地址:", Location = new Point(413, 20), AutoSize = true };
            cmbSlaveAddr = new ComboBox { Location = new Point(483, 17), Width = 85, DropDownStyle = ComboBoxStyle.DropDownList, ItemHeight = 25 };
            cmbSlaveAddr.Items.AddRange(new object[] { "0xF1", "0xF2", "0xF3", "0xF4" });
            cmbSlaveAddr.SelectedIndex = 0;
            _toolTip.SetToolTip(cmbSlaveAddr, "热泵拨码地址：00=F1，01=F2，10=F3，11=F4");

            var lblPoll = new Label { Text = "轮询(ms):", Location = new Point(583, 20), AutoSize = true };
            nudInterval = new NumericUpDown { Location = new Point(655, 17), Width = 80, Height = 28, Minimum = 500, Maximum = 10000, Value = 2000, Increment = 500 };


            btnConnect = new Button { Text = "连接", Location = new Point(750, 16), Width = 80, Height = 28 };
            btnConnect.Click += BtnConnect_Click;

            lblConnIndicator = new Label { Text = "", Location = new Point(836, 20), AutoSize = true, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold), ForeColor = Color.Gray };

            lblStatus = new Label { Text = "未连接", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 4, 18, 0) };
            lblWorkMode = new Label { Text = "工作状态: --", AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold), Margin = new Padding(0, 4, 18, 0) };
            lblRuntimeMode = new Label { Text = "运行模式1: --", AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold), ForeColor = Color.Red, Margin = new Padding(0, 4, 18, 0) };
            lblRuntimeMode2 = new Label { Text = "运行模式2: --", AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold), ForeColor = Color.Red, Margin = new Padding(0, 4, 18, 0) };
            lblSetMode = new Label { Text = "设置模式: --", AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold), ForeColor = Color.Black, Margin = new Padding(0, 4, 18, 0) };
            lblFaultCode = new Label { Text = "故障汇总: 等待读取", AutoSize = true, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold), ForeColor = Color.Gray, Margin = new Padding(0, 4, 18, 0) };
            lblUpdateTime = new Label { Text = "", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 4, 0, 0) };

            var connectionSummary = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 30,
                Padding = new Padding(0),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };
            connectionSummary.Controls.AddRange(new Control[]
            {
                lblStatus, lblWorkMode, lblRuntimeMode, lblRuntimeMode2, lblSetMode, lblFaultCode, lblUpdateTime
            });

            panelTop.Controls.AddRange(new Control[] {
                lblPort, cmbPort, btnRefresh, lblBaud, cmbBaudRate,
                lblSlave, cmbSlaveAddr, lblPoll, nudInterval, btnConnect,
                lblConnIndicator, connectionSummary
            });

            // =============================================
            //  底部：通信日志面板（可隐藏）
            // =============================================
            panelLog = new Panel { Dock = DockStyle.Bottom, Height = 200, Padding = new Padding(2), Visible = true };

            var logHeader = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(240, 240, 240) };
            var lblLogTitle = new Label { Text = "📋 通信日志", Location = new Point(5, 8), AutoSize = true, Font = new Font("Microsoft YaHei", 10, FontStyle.Bold) };
            btnToggleLog = new Button { Text = "▼ 隐藏", Location = new Point(panelLog.Width - 90, 4), Width = 85, Height = 28, Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9) };
            btnToggleLog.Click += (s, e) => ToggleLog();
            chkVerboseLog = new CheckBox
            {
                Text = "详细帧",
                AutoSize = true,
                Location = new Point(105, 8),
                Font = new Font("Microsoft YaHei", 9),
                Checked = false
            };
            chkVerboseLog.CheckedChanged += (s, e) =>
            {
                _verboseCommunicationLog = chkVerboseLog.Checked;
                if (!_verboseCommunicationLog)
                    while (_pendingLogLines.TryDequeue(out _)) { }
            };
            logHeader.Controls.AddRange(new Control[] { lblLogTitle, chkVerboseLog, btnToggleLog });

            rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(50, 50, 50),
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            panelLog.Controls.Add(rtbLog);
            panelLog.Controls.Add(logHeader);
            _logFlushTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _logFlushTimer.Tick += (s, e) => FlushPendingLogLines();
            _logFlushTimer.Start();

            _chartRenderTimer = new System.Windows.Forms.Timer { Interval = (int)ChartRenderInterval.TotalMilliseconds };
            _chartRenderTimer.Tick += (s, e) => RefreshVisibleChart();
            _chartRenderTimer.Start();

            // =============================================
            //  中间主区域：左右分割（数据 | 曲线+故障）
            // =============================================
            var splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
            };

            dgvData = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(245, 245, 245) },
                ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(60, 120, 200), ForeColor = Color.White, Font = new Font("Microsoft YaHei", 9, FontStyle.Bold) },
            };
            dgvData.Columns.Add("Name", "参数名称");
            dgvData.Columns.Add("Value", "当前值");
            dgvData.Columns.Add("Unit", "单位");
            dgvData.Columns.Add("Address", "寄存器");
            dgvData.Columns.Add("Access", "属性");
            dgvData.Columns.Add(new DataGridViewButtonColumn
            {
                Name = "Action",
                HeaderText = "操作",
                FlatStyle = FlatStyle.Flat,
                UseColumnTextForButtonValue = false
            });
            foreach (DataGridViewColumn col in dgvData.Columns)
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
            dgvData.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvData.Columns[0].Width = 150;
            dgvData.Columns[1].Width = 82;
            dgvData.Columns[2].Width = 42;
            dgvData.Columns[3].Width = 60;
            dgvData.Columns[4].Width = 48;
            dgvData.Columns[5].Width = 52;

            // 地址和属性属于诊断信息，默认隐藏以扩大左侧当前值区域；可按需展开。
            dgvData.Columns["Address"].Visible = false;
            dgvData.Columns["Access"].Visible = false;
            bool registerDetailsVisible = false;
            var btnToggleRegisterDetails = new Button
            {
                Text = "显示地址/属性",
                Location = new Point(925, 16),
                Width = 125,
                Height = 28,
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Microsoft YaHei", 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnToggleRegisterDetails.Click += (s, e) =>
            {
                registerDetailsVisible = !registerDetailsVisible;
                dgvData.Columns["Address"].Visible = registerDetailsVisible;
                dgvData.Columns["Access"].Visible = registerDetailsVisible;
                btnToggleRegisterDetails.Text = registerDetailsVisible ? "隐藏地址/属性" : "显示地址/属性";
                splitMain.SplitterDistance = registerDetailsVisible ? LeftPanelTargetWidth : LeftPanelCompactWidth;
            };
            _toolTip.SetToolTip(btnToggleRegisterDetails, "切换显示左侧表格的寄存器地址和属性列");
            panelTop.Controls.Add(btnToggleRegisterDetails);

            // 折叠/展开某分组
            void ToggleGroup(int groupIdx, bool collapse)
            {
                _groupCollapsed[groupIdx] = collapse;
                string name = dgvData.Rows[groupIdx].Cells[0].Value?.ToString() ?? "";
                string clean = name.TrimStart('▶', '▼', ' ');
                dgvData.Rows[groupIdx].Cells[0].Value = (collapse ? "▶ " : "▼ ") + clean;
                for (int i = groupIdx + 1; i < dgvData.Rows.Count; i++)
                {
                    if (_groupRows.Contains(i)) break;
                    dgvData.Rows[i].Visible = !collapse;
                }
            }

            // 点击分组行任意位置触发展开/折叠
            dgvData.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                if (!_groupRows.Contains(e.RowIndex)) return;
                bool wasCollapsed = _groupCollapsed.ContainsKey(e.RowIndex) && _groupCollapsed[e.RowIndex];
                ToggleGroup(e.RowIndex, !wasCollapsed);
                dgvData.ClearSelection();
            };

            // 鼠标移到分组行变手型光标
            dgvData.CellMouseEnter += (s, e) =>
            {
                if (e.RowIndex >= 0 && _groupRows.Contains(e.RowIndex))
                    dgvData.Cursor = Cursors.Hand;
                else
                    dgvData.Cursor = Cursors.Default;
            };
            dgvData.CellContentClick += async (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == dgvData.Columns["Action"].Index)
                    await WriteRegisterFromRowAsync(e.RowIndex);
            };

            // --- 右侧：状态+故障+曲线 ---
            var splitRight = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
            };

            // 右上：故障报警列表。工作状态和故障汇总已移到顶部连接信息后。
            var panelStatus = new Panel { Dock = DockStyle.Fill, Padding = new Padding(5) };

            grpFaultList = new GroupBox { Text = "故障报警列表", Dock = DockStyle.Fill, Padding = new Padding(2) };
            lstFaults = new ListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9),
                BorderStyle = BorderStyle.FixedSingle
            };
            grpFaultList.Controls.Add(lstFaults);

            panelStatus.Controls.Add(grpFaultList);

            var btnToggleFaultList = new Button
            {
                Text = "隐藏故障列表",
                Location = new Point(1058, 16),
                Width = 115,
                Height = 28,
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Microsoft YaHei", 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnToggleFaultList.Click += (s, e) =>
            {
                bool showFaultList = !splitRight.Panel1Collapsed;
                splitRight.Panel1Collapsed = showFaultList;
                btnToggleFaultList.Text = showFaultList ? "隐藏故障列表" : "显示故障列表";
            };
            _toolTip.SetToolTip(btnToggleFaultList, "切换右侧故障列表显示状态");
            panelTop.Controls.Add(btnToggleFaultList);

            // 右下：实时曲线分页
            chartTabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 9F),
                Padding = new Point(14, 4)
            };
            var numericChartPage = new TabPage("参数页") { Padding = new Padding(0) };

            // 第一页：温度、频率、功率等连续数值。
            formsPlot = new ScottPlot.WinForms.FormsPlot { Dock = DockStyle.Fill };
            formsPlot.MouseDown += (s, e) =>
            {
                BeginChartInteraction();
                BeginChartPointer(formsPlot, e.Location);
            };
            formsPlot.MouseWheel += (s, e) =>
            {
                BeginChartInteraction();
                _followCurrentTime = false;
                EndChartInteraction(formsPlot);
            };
            formsPlot.MouseUp += (s, e) => EndChartInteraction(formsPlot);
            formsPlot.MouseCaptureChanged += (s, e) =>
            {
                if (!formsPlot.Capture && Control.MouseButtons == MouseButtons.None)
                    EndChartInteraction(formsPlot);
            };
            formsPlot.MouseMove += (s, e) =>
            {
                UpdateChartPointerDrag(formsPlot, e, () => _followCurrentTime = false);
                FormsPlot_MouseMove(s, e);
            };
            formsPlot.MouseLeave += (s, e) => _chartToolTip.Hide(formsPlot);
            var chart = formsPlot.Plot;
            var dtTickGen = new ScottPlot.TickGenerators.DateTimeAutomatic();
            dtTickGen.LabelFormatter = dt => dt.ToString("HH:mm:ss");
            chart.Axes.Bottom.TickGenerator = dtTickGen;
            chart.Legend.IsVisible = false;
            // 参数页不再显示右轴曲线，隐藏右侧轴线及其数字刻度，避免空右轴占用绘图区。
            chart.Axes.Right.IsVisible = false;

            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                _numericCurveData[definition.Address] = new List<double>();
                if (definition.SelectedByDefault)
                    SetNumericCurveVisibility(definition, true);
            }

            numericSelector = new TreeView
            {
                Dock = DockStyle.Right,
                Width = 175,
                CheckBoxes = true,
                HideSelection = false,
                ShowLines = true,
                ShowPlusMinus = true,
                Font = new Font("Microsoft YaHei", 9F)
            };
            foreach (var group in NumericCurveDefinitions.GroupBy(item => item.GroupName))
            {
                var groupNode = new TreeNode(group.Key);
                foreach (NumericCurveDefinition definition in group.Where(item => item.Address != 30233))
                {
                    groupNode.Nodes.Add(new TreeNode(definition.Name)
                    {
                        Tag = definition,
                        Checked = definition.SelectedByDefault
                    });
                }
                if (groupNode.Nodes.Count > 0)
                    numericSelector.Nodes.Add(groupNode);
            }
            foreach (TreeNode node in numericSelector.Nodes)
                node.Expand();

            var btnToggleCurveSelector = new Button
            {
                Text = "隐藏曲线选择",
                Location = new Point(1178, 16),
                Width = 115,
                Height = 28,
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Microsoft YaHei", 9),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnToggleCurveSelector.Click += (s, e) =>
            {
                // 三个页面的曲线选择器同步显示/隐藏，任一选中状态在切换 Tab 后依然生效。
                bool showSelector = ApplyTreeSelectorToggle(numericSelector, bitSelector, stateSelector);
                btnToggleCurveSelector.Text = showSelector ? "隐藏曲线选择" : "显示曲线选择";
            };
            _toolTip.SetToolTip(btnToggleCurveSelector, "切换参数页/状态页/故障页右侧曲线选择区域");
            panelTop.Controls.Add(btnToggleCurveSelector);

            bool updatingNumericChecks = false;
            numericSelector.AfterCheck += (s, e) =>
            {
                if (updatingNumericChecks || e.Node is not TreeNode checkedNode)
                    return;

                if (checkedNode.Tag is NumericCurveDefinition definition)
                {
                    SetNumericCurveVisibility(definition, checkedNode.Checked);
                }
                else
                {
                    updatingNumericChecks = true;
                    foreach (TreeNode child in checkedNode.Nodes)
                    {
                        child.Checked = checkedNode.Checked;
                        if (child.Tag is NumericCurveDefinition childDefinition)
                            SetNumericCurveVisibility(childDefinition, child.Checked);
                    }
                    updatingNumericChecks = false;
                }

                if (_followCurrentTime)
                    AutoScaleChart();
                else
                    formsPlot.Refresh();
            };

            // 曲线工具按钮浮动于绘图区左上角。
            btnBackToNow = new Button
            {
                Text = "⟲ 回到当前",
                FlatStyle = FlatStyle.Flat,
                Location = new Point(10, 10),
                Size = new Size(100, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnBackToNow.Click += (s, e) => BackToNow();

            var btnExportLog = new Button
            {
                Text = "导出LOG",
                FlatStyle = FlatStyle.Flat,
                Location = new Point(116, 10),
                Size = new Size(90, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnExportLog.Click += async (s, e) => await ExportCurveDataAsync(false);

            var btnExportExcel = new Button
            {
                Text = "导出Excel",
                FlatStyle = FlatStyle.Flat,
                Location = new Point(212, 10),
                Size = new Size(96, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnExportExcel.Click += async (s, e) => await ExportCurveDataAsync(true);

            var btnOpenHistory = new Button
            {
                Text = "打开历史",
                FlatStyle = FlatStyle.Flat,
                Location = new Point(314, 10),
                Size = new Size(96, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnOpenHistory.Click += async (s, e) => await OpenCurveHistoryAsync();

            btnArchiveFolder = new Button
            {
                Text = "存档路径",
                FlatStyle = FlatStyle.Flat,
                Size = new Size(90, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnArchiveFolder.Click += (s, e) => ChooseArchiveFolder();
            _toolTip.SetToolTip(btnArchiveFolder, _archiveFolder);

            var numericChartContent = new Panel { Dock = DockStyle.Fill };
            numericChartContent.Controls.Add(formsPlot);
            numericChartContent.Controls.Add(numericSelector);

            var curveToolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(5),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(248, 248, 248),
                Font = new Font("Microsoft YaHei", 9F)
            };
            foreach (Button button in new[] { btnBackToNow, btnExportLog, btnExportExcel, btnOpenHistory, btnArchiveFolder })
            {
                button.Location = Point.Empty;
                button.Margin = new Padding(0, 0, 4, 0);
                curveToolbar.Controls.Add(button);
            }
            curveToolbar.Controls.Add(new Label
            {
                Text = "范围:",
                AutoSize = true,
                Margin = new Padding(2, 7, 2, 0)
            });
            curveToolbar.Controls.Add(CreateChartRangeSelector());

            numericChartPage.Controls.Add(numericChartContent);
            numericChartPage.Controls.Add(curveToolbar);

            // BIT 历史数据由故障页和状态页共享，但每条曲线只显示在所属页面。
            foreach (ushort address in BitCurveRegisterAddresses)
                _allBitRegisterData[address] = new List<ushort>();

            TabPage CreateBitChartPage(
                string pageName,
                IReadOnlyList<BitCurveDefinition> definitions,
                ScottPlot.WinForms.FormsPlot plot,
                Func<bool> isFollowing,
                Action stopFollowing,
                Action backToNow,
                out Button backButton,
                out TreeView pageSelector)
            {
                var page = new TabPage(pageName) { Padding = new Padding(0) };
                plot.Dock = DockStyle.Fill;
                plot.MouseDown += (s, e) =>
                {
                    BeginChartInteraction();
                    BeginChartPointer(plot, e.Location);
                };
                plot.MouseWheel += (s, e) =>
                {
                    BeginChartInteraction();
                    stopFollowing();
                    EndChartInteraction(plot);
                };
                plot.MouseUp += (s, e) => EndChartInteraction(plot);
                plot.MouseCaptureChanged += (s, e) =>
                {
                    if (!plot.Capture && Control.MouseButtons == MouseButtons.None)
                        EndChartInteraction(plot);
                };
                plot.MouseMove += (s, e) =>
                {
                    UpdateChartPointerDrag(plot, e, stopFollowing);
                    BitFormsPlot_MouseMove(s, e);
                };
                plot.MouseLeave += (s, e) => _chartToolTip.Hide(plot);

                var dtTickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic();
                dtTickGenerator.LabelFormatter = dt => dt.ToString("HH:mm:ss");
                plot.Plot.Axes.Bottom.TickGenerator = dtTickGenerator;
                plot.Plot.Legend.IsVisible = false;

                foreach (BitCurveDefinition definition in definitions)
                {
                    if (definition.SelectedByDefault)
                        SetBitCurveVisibility(definition, true);
                }

                var selector = new TreeView
                {
                    Dock = DockStyle.Right,
                    Width = 220,
                    CheckBoxes = true,
                    HideSelection = false,
                    ShowLines = true,
                    ShowPlusMinus = true,
                Font = new Font("Microsoft YaHei", 9F)
                };
                pageSelector = selector;
                foreach (var group in definitions.GroupBy(item => new { item.Address, item.GroupName }))
                {
                    var groupNode = new TreeNode(group.Key.GroupName);
                    foreach (BitCurveDefinition definition in group)
                    {
                        groupNode.Nodes.Add(new TreeNode(definition.Name)
                        {
                            Tag = definition,
                            Checked = definition.SelectedByDefault
                        });
                    }
                    selector.Nodes.Add(groupNode);
                }
                foreach (TreeNode node in selector.Nodes)
                    node.Expand();

                bool updatingChecks = false;
                selector.AfterCheck += (s, e) =>
                {
                    if (updatingChecks || e.Node is not TreeNode checkedNode)
                        return;

                    if (checkedNode.Tag is BitCurveDefinition definition)
                    {
                        SetBitCurveVisibility(definition, checkedNode.Checked);
                    }
                    else
                    {
                        updatingChecks = true;
                        foreach (TreeNode child in checkedNode.Nodes)
                        {
                            child.Checked = checkedNode.Checked;
                            if (child.Tag is BitCurveDefinition childDefinition)
                                SetBitCurveVisibility(childDefinition, child.Checked);
                        }
                        updatingChecks = false;
                    }

                    if (isFollowing())
                        AutoScaleBitChart(plot);
                    else
                        plot.Refresh();
                };

                backButton = new Button
                {
                    Text = "⟲ 回到当前",
                    FlatStyle = FlatStyle.Flat,
                    Location = new Point(10, 10),
                    Size = new Size(100, 28),
                    Font = new Font("Microsoft YaHei", 9F),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left
                };
                backButton.Click += (s, e) => backToNow();

                var historyRangeLabel = new Label
                {
                    Text = "历史范围:",
                    AutoSize = true,
                    BackColor = Color.White,
                    Location = new Point(120, 17),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left
                };
                ComboBox historyRangeSelector = CreateChartRangeSelector();
                historyRangeSelector.Location = new Point(184, 12);
                historyRangeSelector.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                page.Controls.Add(plot);
                page.Controls.Add(selector);
                page.Controls.Add(backButton);
                page.Controls.Add(historyRangeLabel);
                page.Controls.Add(historyRangeSelector);
                backButton.BringToFront();
                historyRangeLabel.BringToFront();
                historyRangeSelector.BringToFront();
                return page;
            }

            bitFormsPlot = new ScottPlot.WinForms.FormsPlot();
            var bitChartPage = CreateBitChartPage(
                "故障页",
                FaultCurveDefinitions,
                bitFormsPlot,
                () => _followBitCurrentTime,
                () => _followBitCurrentTime = false,
                BackToBitNow,
                out btnBitBackToNow,
                out bitSelector);

            stateFormsPlot = new ScottPlot.WinForms.FormsPlot();
            var stateChartPage = CreateBitChartPage(
                "状态页",
                StatusCurveDefinitions,
                stateFormsPlot,
                () => _followStateCurrentTime,
                () => _followStateCurrentTime = false,
                BackToStateNow,
                out btnStateBackToNow,
                out stateSelector);

            chartTabs.TabPages.Add(numericChartPage);
            chartTabs.TabPages.Add(stateChartPage);
            chartTabs.TabPages.Add(bitChartPage);
            chartTabs.SelectedIndexChanged += (s, e) => RefreshVisibleChart(true);

            splitRight.Panel1.Controls.Add(panelStatus);
            splitRight.Panel2.Controls.Add(chartTabs);

            splitMain.Panel1.Controls.Add(dgvData);
            splitMain.Panel2.Controls.Add(splitRight);

            // =============================================
            //  组装
            // =============================================
            Controls.Add(splitMain);
            Controls.Add(panelLog);
            Controls.Add(panelTop);

            RefreshPorts();

            // 延迟设置 SplitterDistance（窗口加载后才能确定尺寸）
            Load += (s, e) =>
            {
                splitMain.FixedPanel = FixedPanel.Panel1;
                splitMain.Panel1MinSize = LeftPanelCompactWidth - 2;
                splitMain.Panel2MinSize = 300;
                splitMain.SplitterDistance = Math.Clamp(
                    LeftPanelCompactWidth,
                    splitMain.Panel1MinSize,
                    Math.Max(splitMain.Panel1MinSize,
                             splitMain.Width - splitMain.SplitterWidth - splitMain.Panel2MinSize));
                splitRight.SplitterDistance = 100;
            };

            // 预填充所有寄存器到表格
            InitRegisterTable();
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
            _followBitCurrentTime = true;
            _followStateCurrentTime = true;
            RefreshAllChartsForSelectedRange();
        }

        private void RefreshAllChartsForSelectedRange()
        {
            try
            {
                _lastChartAutoScaleAt = DateTime.MinValue;
                AutoScaleChart();
                AutoScaleBitChart(bitFormsPlot);
                AutoScaleBitChart(stateFormsPlot);
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
                    UpdateChartRenderRange(plot);
                    plot.Refresh();
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
                switch (chartTabs.SelectedIndex)
                {
                    case 0:
                        if (_followCurrentTime && shouldAutoScale)
                        {
                            _lastChartAutoScaleAt = DateTime.UtcNow;
                            AutoScaleChart();
                        }
                        else
                        {
                            // 用户拖动/缩放后保留当前视口，同时扩展可见数据范围并继续实时重绘。
                            UpdateChartRenderRange(formsPlot);
                            formsPlot.Refresh();
                        }
                        break;
                    case 1:
                        if (_followStateCurrentTime && shouldAutoScale)
                        {
                            _lastChartAutoScaleAt = DateTime.UtcNow;
                            AutoScaleBitChart(stateFormsPlot);
                        }
                        else
                        {
                            UpdateChartRenderRange(stateFormsPlot);
                            stateFormsPlot.Refresh();
                        }
                        break;
                    case 2:
                        if (_followBitCurrentTime && shouldAutoScale)
                        {
                            _lastChartAutoScaleAt = DateTime.UtcNow;
                            AutoScaleBitChart(bitFormsPlot);
                        }
                        else
                        {
                            UpdateChartRenderRange(bitFormsPlot);
                            bitFormsPlot.Refresh();
                        }
                        break;
                }
                _chartDataDirty = false;
            }
            catch { }
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

            IReadOnlyList<BitCurveDefinition> definitions = plot == stateFormsPlot
                ? StatusCurveDefinitions
                : FaultCurveDefinitions;
            foreach (BitCurveDefinition definition in definitions)
            {
                if (_bitCurvePlottables.TryGetValue(
                    (definition.Address, definition.Bit),
                    out ScottPlot.IPlottable? curve))
                {
                    ApplyRange(curve, minimumIndex, maximumIndex);
                }
            }
        }

        // ==================== 寄存器表格初始化 ====================

        private void InitRegisterTable()
        {
            // 分组标题用灰色背景行
            void AddGroup(string title)
            {
                int idx = dgvData.Rows.Add("▶ " + title, "", "", "", "", "");
                dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                dgvData.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(220, 225, 235);
                dgvData.Rows[idx].DefaultCellStyle.Font = new Font("Microsoft YaHei", 9, FontStyle.Bold);
                dgvData.Rows[idx].DefaultCellStyle.ForeColor = Color.FromArgb(40, 40, 120);
                dgvData.Rows[idx].DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 225, 235);
                dgvData.Rows[idx].DefaultCellStyle.SelectionForeColor = Color.FromArgb(40, 40, 120);
                _groupRows.Add(idx);
                _groupCollapsed[idx] = false;
            }

            void AddReg(ushort addr, string name, string unit)
            {
                RegisterAccess access = RegisterMap.GetAccess(addr);
                string accessText = access switch
                {
                    RegisterAccess.ReadWrite => "读写",
                    RegisterAccess.WriteOnly => "只写",
                    _ => "只读"
                };
                string currentValue = access == RegisterAccess.WriteOnly ? "—" : "--";
                string actionText = access == RegisterAccess.ReadWrite ? "写入" : "执行";
                int idx = dgvData.Rows.Add("   " + name, currentValue, unit, addr.ToString(), accessText, actionText);
                dgvData.Rows[idx].Tag = addr;
                if (access == RegisterAccess.ReadOnly)
                    dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                _rowMap[addr] = idx;
            }

            void AddBitReg(ushort addr, int bit, string name)
            {
                int idx = dgvData.Rows.Add(
                    "   " + name,
                    "--",
                    "位",
                    $"{addr} BIT{bit}",
                    "只读",
                    "");
                dgvData.Rows[idx].Tag = (addr, bit);
                dgvData.Rows[idx].Cells["Action"] = new DataGridViewTextBoxCell { Value = "" };
                _bitRowMap[(addr, bit)] = idx;
            }

            void AddFaultBits(ushort address, Dictionary<int, string> definitions)
            {
                foreach (var definition in definitions.OrderBy(item => item.Key))
                    AddBitReg(address, definition.Key, definition.Value);
            }

            void AddStatusBits(ushort address, Dictionary<int, BitDefinition> definitions)
            {
                foreach (var definition in definitions.OrderBy(item => item.Key))
                    AddBitReg(address, definition.Key, definition.Value.Name);
            }

            // --- 版本信息 ---
            AddGroup("【版本信息】");
            AddReg(30001, "主控板硬件版本号", "");
            AddReg(30003, "主控板软件版本号", "");
            AddReg(30005, "产品型号", "");
            AddReg(30009, "压缩机驱动板软件版本号", "");
            AddReg(30011, "风机驱动板软件版本号", "");

            // --- 故障报警 ---
            AddGroup("【故障报警状态】");
            AddFaultBits(30101, RegisterMap.FaultReg1Bits);
            AddFaultBits(30102, RegisterMap.FaultReg2Bits);
            AddFaultBits(30103, RegisterMap.FaultReg3Bits);
            AddFaultBits(30104, RegisterMap.FaultReg4Bits);
            AddFaultBits(30105, RegisterMap.FaultReg5Bits);

            // --- 工作状态 ---
            AddGroup("【工作状态】");
            AddStatusBits(30106, RegisterMap.StatusWordBits);
            AddStatusBits(30201, RegisterMap.DeviceStatusBits);
            AddReg(30221, "压缩机频率", "Hz");
            AddReg(30234, "压缩机目标频率", "Hz");
            AddReg(30109, "压缩机频率（快速）", "Hz");
            AddReg(30222, "上风机转速", "RPM");
            AddReg(30225, "下风机转速", "RPM");
            // The value cell includes the minute/second suffix for these durations.
            AddReg(30235, "压缩机运行时间", "");
            AddReg(30237, "压缩机停止时间", "");
            AddReg(30219, "主路EXV开度", "步");
            AddReg(30220, "辅路EXV开度", "步");
            AddReg(30223, "电动三通球阀1", "步");
            AddReg(30224, "电动三通球阀2", "步");

            AddGroup("【通信与升级状态】");
            AddReg(30107, "热泵主机升级状态", "");
            AddReg(30108, "空调进水温度（快速）", "℃");
            AddReg(30110, "4G写保持寄存器计数", "次");
            AddStatusBits(30229, RegisterMap.DipSwitchBits);

            // --- 温度数据 ---
            AddGroup("【温度数据】");
            AddReg(30202, "排气温度", "℃");
            AddReg(30203, "热回收出口温度", "℃");
            AddReg(30204, "中盘温度", "℃");
            AddReg(30205, "出盘温度", "℃");
            AddReg(30206, "经济器进口温度", "℃");
            AddReg(30207, "经济器出口温度", "℃");
            AddReg(30208, "吸气温度", "℃");
            AddReg(30209, "空调进水温度", "℃");
            AddReg(30210, "空调出水温度", "℃");
            AddReg(30211, "环境温度", "℃");
            AddReg(30212, "热水进水温度", "℃");
            AddReg(30213, "热水出水温度", "℃");
            AddReg(30214, "热水温度", "℃");
            AddReg(30226, "热水上温度", "℃");
            AddReg(30227, "热水中温度", "℃");
            AddReg(30228, "热水下温度", "℃");
            AddReg(30242, "压缩机IPM模块温度", "℃");
            AddReg(30215, "低压压力", "bar");
            AddReg(30216, "高压压力", "bar");
            AddReg(30217, "累计耗电量", "kWh");

            AddGroup("【电气数据】");
            AddReg(30231, "AC电压", "V");
            AddReg(30232, "AC电流", "A");
            AddReg(30233, "当前功率", "W");
            AddReg(30239, "DC电压", "V");
            AddReg(30240, "压缩机电流", "A");
            AddReg(30241, "DC风机电流", "A");

            // --- 保持寄存器（常用命令） ---
            AddGroup("【常用命令 - 保持寄存器】");
            AddReg(40001, "开关机", "");
            AddReg(40002, "静音模式设置", "");
            AddReg(40003, "零冷水开关", "");
            AddReg(40004, "零冷水开启时长", "min");
            AddReg(40005, "零冷水剩余时长", "min");
            AddReg(40006, "手动除霜", "");
            AddReg(40007, "热水杀菌开关", "");
            AddReg(40008, "防冻电加热开关", "");
            AddReg(40009, "热水增程阀开关（W180）", "");
            AddReg(40010, "零冷水点动开关", "");
            AddReg(40011, "主机强制待机（W180）", "");
            AddReg(40012, "室内设备运行数量（W180）", "台");

            // --- 服务密码段 ---
            AddGroup("【服务密码段 - 温度设置】");
            AddReg(40201, "工作模式设置", "");
            AddReg(40202, "生活热水启用", "");
            AddReg(40203, "零冷水泵支持状态", "");
            AddReg(40204, "制冷进水温度设置", "℃");
            AddReg(40205, "制冷出水温度设置", "℃");
            AddReg(40206, "制热进水温度设置", "℃");
            AddReg(40207, "制热出水温度设置", "℃");
            AddReg(40208, "温度控制依据", "");
            AddReg(40209, "能力计算周期", "s");
            AddReg(40210, "空调停机温差", "℃");
            AddReg(40211, "空调启动温差", "℃");
            AddReg(40212, "生活热水模式", "");
            AddReg(40214, "空调水泵预运行时间", "s");
            AddReg(40215, "冬季防冻温度", "℃");
            AddReg(40216, "制冷防冻温度", "℃");
            AddReg(40217, "除霜检测A点（DFA）", "℃");
            AddReg(40218, "除霜检测B点（DFB）", "℃");
            AddReg(40219, "除霜退出温度", "℃");
            AddReg(40220, "除霜最长运行时间", "min");
            AddReg(40221, "除霜间隔时间", "min");
            AddReg(40222, "允许除霜最高环温", "℃");
            AddReg(40223, "待机水泵运行方式", "");
            AddReg(40224, "待机水泵交替间隔", "min");
            AddReg(40225, "待机水泵交替运行时间", "min");
            AddReg(40226, "压缩机预热启用", "");
            AddReg(40227, "断电记忆启用", "");
            AddReg(40228, "空调循环水泵开关", "");
            AddReg(40229, "热水水泵开关", "");
            AddReg(40230, "零冷水水泵开关", "");
            AddReg(40231, "零冷水点动运行时长", "min");
            AddReg(40232, "零冷水水泵开启时长", "min");
            AddReg(40233, "零冷水水泵关闭时长", "min");
            AddReg(40235, "零冷水整点启动时长", "s");
            AddReg(40236, "零冷水泵启动水温", "℃");
            AddReg(40237, "零冷水启动持续时间", "s");
            AddReg(40238, "热水进水不可用温度", "℃");
            AddReg(40239, "空调水泵延时打开时长（W180）", "s");

            // --- 工厂密码段 ---
            AddGroup("【工厂密码段】");
            AddReg(40301, "制热进水温度补偿", "℃");
            AddReg(40302, "制热出水温度补偿", "℃");
            AddReg(40303, "制冷进水温度补偿", "℃");
            AddReg(40304, "制冷出水温度补偿", "℃");
            AddReg(40305, "压缩机最高频率", "Hz");
            AddReg(40306, "压缩机最低频率", "Hz");
            AddReg(40307, "风机最高转速", "RPM");
            AddReg(40308, "目标过热度", "℃");
            AddReg(40309, "主路EXV控制周期", "s");
            AddReg(40310, "辅路EXV控制周期", "s");
            AddReg(40311, "制冷共振频率1", "Hz");
            AddReg(40312, "制冷共振频率2", "Hz");
            AddReg(40313, "制冷共振频率3", "Hz");
            AddReg(40314, "制冷共振频率4", "Hz");
            AddReg(40315, "制冷共振频率5", "Hz");
            AddReg(40316, "制热共振频率1", "Hz");
            AddReg(40317, "制热共振频率2", "Hz");
            AddReg(40318, "制热共振频率3", "Hz");
            AddReg(40319, "制热共振频率4", "Hz");
            AddReg(40320, "制热共振频率5", "Hz");
            AddReg(40321, "制热最高水温修正K1", "");
            AddReg(40322, "制热最高水温修正K2", "℃");
            AddReg(40323, "制热最高水温修正K3", "");
            AddReg(40324, "制热最高水温修正K4", "℃");

            // --- 只写命令 ---
            AddGroup("【只写命令】");
            AddReg(40101, "手动解除故障报警", "");
            AddReg(40102, "同步天气温度(4G)", "℃");
            AddReg(40103, "除霜请求控制", "");
            AddReg(40105, "通信中断超2小时标志", "");
            AddReg(40106, "清除设备信息", "");
            AddReg(40111, "同步天气湿度(4G)", "%");
        }

        private async Task WriteRegisterFromRowAsync(int rowIndex)
        {
            if (_writeInProgress || rowIndex < 0 || rowIndex >= dgvData.Rows.Count)
                return;
            if (dgvData.Rows[rowIndex].Tag is not ushort address)
                return;

            RegisterAccess access = RegisterMap.GetAccess(address);
            if (access == RegisterAccess.ReadOnly)
                return;
            if (!_modbus.IsConnected)
            {
                MessageBox.Show("请先连接串口，再写入参数。", "未连接", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            DataGridViewRow row = dgvData.Rows[rowIndex];
            string name = row.Cells["Name"].Value?.ToString()?.Trim() ?? address.ToString();
            string unit = row.Cells["Unit"].Value?.ToString() ?? "";
            string currentValue = row.Cells["Value"].Value?.ToString() ?? "";
            if (!TryShowWriteDialog(address, name, unit, currentValue, out string input))
                return;
            if (!TryEncodeParameterValue(address, input, out ushort rawValue, out string error))
            {
                MessageBox.Show(error, "输入无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string displayValue = FormatParameterValue(address, rawValue);
            string operation = access == RegisterAccess.WriteOnly ? "执行" : "写入";
            DialogResult confirmed = MessageBox.Show(
                $"参数：{name}\n寄存器：{address}\n输入值：{displayValue}{unit}\n协议值：{rawValue} (0x{rawValue:X4})\n\n确认{operation}？",
                $"确认{operation}",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (confirmed != DialogResult.OK)
                return;

            DataGridViewCell actionCell = row.Cells["Action"];
            _writeInProgress = true;
            actionCell.Value = "处理中";
            _pollTimer?.Stop();
            try
            {
                ushort? readBack = await Task.Run<ushort?>(() =>
                {
                    _modbus.WriteSingleRegister(address, rawValue);
                    if (access == RegisterAccess.ReadWrite)
                        return _modbus.ReadHoldingRegisters(address, 1)[0];
                    return null;
                });

                if (readBack.HasValue)
                {
                    SetRegisterDisplayValue(address, FormatParameterValue(address, readBack.Value));
                    MessageBox.Show(
                        $"写入成功，设备回读值：{FormatParameterValue(address, readBack.Value)}{unit}",
                        "写入成功",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show("命令已发送并收到设备应答。只写寄存器没有可回读的当前值。", "执行成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                AppendLog("ERR", $"写入 {address} 失败: {ex.Message}");
                MessageBox.Show($"写入失败：{ex.Message}", "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                actionCell.Value = operation;
                _writeInProgress = false;
                if (_isPolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private bool TryShowWriteDialog(
            ushort address,
            string name,
            string unit,
            string currentValue,
            out string input)
        {
            using var dialog = new Form
            {
                Text = RegisterMap.GetAccess(address) == RegisterAccess.WriteOnly ? "执行寄存器命令" : "写入参数",
                ClientSize = new Size(420, 190),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent
            };
            var lblName = new Label
            {
                Text = $"{name}  [{address}]",
                Location = new Point(16, 15),
                AutoSize = true,
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold)
            };
            var lblCurrent = new Label
            {
                Text = RegisterMap.GetAccess(address) == RegisterAccess.WriteOnly
                    ? "当前值：只写命令不可读取"
                    : $"当前值：{currentValue}{unit}",
                Location = new Point(16, 45),
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            var lblValue = new Label { Text = "设置值：", Location = new Point(16, 79), AutoSize = true };
            var txtValue = new TextBox
            {
                Location = new Point(78, 75),
                Width = 185,
                Text = currentValue is "--" or "—" ? "" : currentValue
            };
            var lblUnit = new Label { Text = unit, Location = new Point(270, 79), AutoSize = true };
            var lblHint = new Label
            {
                Text = BuildWriteHint(address, unit),
                Location = new Point(16, 109),
                Size = new Size(388, 35),
                ForeColor = Color.DimGray
            };
            var btnOk = new Button { Text = "确定", Location = new Point(238, 150), Size = new Size(78, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "取消", Location = new Point(326, 150), Size = new Size(78, 28), DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange(new Control[] { lblName, lblCurrent, lblValue, txtValue, lblUnit, lblHint, btnOk, btnCancel });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;
            dialog.Shown += (s, e) => { txtValue.SelectAll(); txtValue.Focus(); };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                input = "";
                return false;
            }

            input = txtValue.Text;
            return true;
        }

        private static string BuildWriteHint(ushort address, string unit)
        {
            if (!WriteRules.TryGetValue(address, out WriteRule? rule))
                return "请输入十进制数或0x开头的十六进制原始值。";

            string suffix = string.IsNullOrEmpty(unit) ? "" : unit;
            string precision = rule.Scale == 10 ? "，支持1位小数" : "";
            string special = rule.AllowFFFF ? "；也可输入0xFFFF" : "";
            return $"允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}{suffix}{precision}{special}";
        }

        internal static bool TryEncodeParameterValue(ushort address, string input, out ushort rawValue, out string error)
        {
            rawValue = 0;
            error = "";
            WriteRule rule = WriteRules.TryGetValue(address, out WriteRule? configured)
                ? configured
                : new WriteRule(0, 65535);
            string text = input.Trim();
            if (text.Length == 0)
            {
                error = "请输入要写入的值。";
                return false;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ushort.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rawValue))
                {
                    error = "十六进制值格式无效。";
                    return false;
                }
                if (rule.AllowFFFF && rawValue == ushort.MaxValue)
                    return true;

                decimal decoded = rule.Signed ? (short)rawValue / rule.Scale : rawValue / rule.Scale;
                if (decoded < rule.Minimum || decoded > rule.Maximum)
                {
                    error = $"数值超出协议允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}。";
                    return false;
                }
                return true;
            }

            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal value) &&
                !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                error = "请输入有效的数字，或输入0x开头的十六进制原始值。";
                return false;
            }
            if (value < rule.Minimum || value > rule.Maximum)
            {
                error = $"数值超出协议允许范围：{rule.Minimum:0.###}～{rule.Maximum:0.###}。";
                return false;
            }

            decimal scaled = value * rule.Scale;
            if (scaled != decimal.Truncate(scaled))
            {
                error = rule.Scale == 10 ? "该参数最多支持1位小数。" : "该参数只支持整数。";
                return false;
            }
            if (rule.Signed)
            {
                int signedValue = decimal.ToInt32(scaled);
                if (signedValue < short.MinValue || signedValue > short.MaxValue)
                {
                    error = "数值超出16位有符号寄存器范围。";
                    return false;
                }
                rawValue = unchecked((ushort)(short)signedValue);
            }
            else
            {
                if (scaled < ushort.MinValue || scaled > ushort.MaxValue)
                {
                    error = "数值超出16位寄存器范围。";
                    return false;
                }
                rawValue = decimal.ToUInt16(scaled);
            }
            return true;
        }

        internal static string FormatParameterValue(ushort address, ushort rawValue)
        {
            if (ParameterValueMeanings.TryGetValue(address, out Dictionary<ushort, string>? meanings))
            {
                string meaning = meanings.TryGetValue(rawValue, out string? mapped)
                    ? mapped
                    : $"未知({rawValue})";
                return $"0x{rawValue:X4} / {meaning}";
            }

            if (!WriteRules.TryGetValue(address, out WriteRule? rule))
                return rawValue.ToString(CultureInfo.CurrentCulture);
            if (rule.AllowFFFF && rawValue == ushort.MaxValue)
                return "0xFFFF";
            if (rule.Scale == 1)
                return rawValue.ToString(CultureInfo.CurrentCulture);

            decimal value = rule.Signed ? (short)rawValue / rule.Scale : rawValue / rule.Scale;
            return value.ToString("0.0", CultureInfo.CurrentCulture);
        }

        // ==================== 通信日志 ====================

        private void ChooseArchiveFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择曲线自动存档目录（每6小时同时保存LOG和Excel）",
                SelectedPath = _archiveFolder,
                ShowNewFolderButton = true,
                UseDescriptionForTitle = true
            };
            // 启动阶段不指定隐藏的主窗体作为 owner，避免文件夹选择框出现在后台而阻塞主界面。
            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            try
            {
                ArchiveSettingsStore.SaveArchiveFolder(dialog.SelectedPath);
                _archiveFolder = Path.GetFullPath(dialog.SelectedPath);
                _toolTip.SetToolTip(btnArchiveFolder, _archiveFolder);
                AppendLog("SYS", $"曲线存档路径: {_archiveFolder}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"无法使用该存档路径：{ex.Message}",
                    "存档路径",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_closeConfirmed)
                return;

            e.Cancel = true;
            if (_closePromptInProgress)
                return;

            _closePromptInProgress = true;
            bool resumePolling = _pollTimer?.Enabled == true;
            _pollTimer?.Stop();
            try
            {
                if (_activeAutomaticArchiveTask is { IsCompleted: false })
                {
                    UseWaitCursor = true;
                    lblStatus.Text = "正在完成曲线自动存档...";
                    // 存档可能因目标盘离线等 IO 问题挂起，限时等待，超时后允许用户强制关闭。
                    Task completed = await Task.WhenAny(
                        _activeAutomaticArchiveTask,
                        Task.Delay(TimeSpan.FromSeconds(30)));
                    if (completed != _activeAutomaticArchiveTask)
                        AppendLog("ERR", "等待自动存档超时（30 秒），未存档数据可在下次启动前手动导出");
                }

                bool hasUnsavedSamples = _timeData.Count > 0
                    && _timeData[^1] > _lastSavedSampleTime;
                if (!hasUnsavedSamples)
                {
                    _closeConfirmed = true;
                    Close();
                    return;
                }

                DialogResult result = MessageBox.Show(
                    $"当前仍有未存档的曲线数据。\n\n是否同时导出 LOG 和 Excel 后再关闭？\n存档路径：{_archiveFolder}",
                    "保存曲线数据",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                if (result == DialogResult.Cancel)
                    return;
                if (result == DialogResult.No)
                {
                    _closeConfirmed = true;
                    Close();
                    return;
                }

                UseWaitCursor = true;
                int startIndex = double.IsNegativeInfinity(_lastSavedSampleTime)
                    ? 0
                    : FindFirstTimeIndexAtOrAfter(
                        _timeData,
                        Math.BitIncrement(_lastSavedSampleTime));
                if (startIndex < _timeData.Count)
                {
                    CompactCurveHistoryData snapshot = CreateCurveHistorySnapshot(startIndex);
                    (string logPath, string excelPath) = await SaveArchivePairAsync(snapshot);
                    MessageBox.Show(
                        $"曲线数据已保存：\n{logPath}\n{excelPath}",
                        "存档完成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                _closeConfirmed = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"关闭前存档失败：{ex.Message}\n\n软件将保持打开，请检查存档路径后重试。",
                    "存档失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                UseWaitCursor = false;
                _closePromptInProgress = false;
                if (!_closeConfirmed && resumePolling && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private void ToggleLog()
        {
            _logVisible = !_logVisible;
            panelLog.Height = _logVisible ? 200 : 36;
            btnToggleLog.Text = _logVisible ? "▼ 隐藏" : "▲ 展开";
        }
        private void FlushPendingLogLines()
        {
            if (IsDisposed || Disposing || rtbLog.IsDisposed)
                return;

            var lines = new List<string>(100);
            while (lines.Count < 100 && _pendingLogLines.TryDequeue(out string? line))
                lines.Add(line);
            if (lines.Count == 0)
                return;

            rtbLog.AppendText(string.Concat(lines));
            rtbLog.ScrollToCaret();
            if (rtbLog.Lines.Length > 500)
            {
                int firstLineToKeep = Math.Max(0, rtbLog.Lines.Length - 300);
                int removeLength = rtbLog.GetFirstCharIndexFromLine(firstLineToKeep);
                if (removeLength > 0)
                {
                    rtbLog.Select(0, removeLength);
                    rtbLog.SelectedText = "";
                }
            }
        }

        /// <summary>记录系统/错误文本消息（SYS/ERR）。</summary>
        private void AppendLog(string direction, string message)
            => AppendLog(direction, System.Text.Encoding.UTF8.GetBytes(message));

        private void AppendLog(string direction, byte[] data)
        {
            if (IsDisposed || Disposing)
                return;

            // TX/RX 帧数量很大，默认只保留系统消息和错误；需要抓包时勾选“详细帧”。
            if (direction is not ("SYS" or "ERR")
                && (!_verboseCommunicationLog || !_logVisible))
                return;

            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string line;
            if (direction is "SYS" or "ERR")
            {
                string message = System.Text.Encoding.UTF8.GetString(data);
                line = $"[{time}] {direction}: {message}\n";
            }
            else
            {
                string hex = BitConverter.ToString(data).Replace("-", " ");
                string arrow = direction == "TX" ? "→" : "←";
                line = $"[{time}] {arrow} {direction}: {hex}\n";
            }

            _pendingLogLines.Enqueue(line);
            if (!InvokeRequired)
                FlushPendingLogLines();
        }

        // ==================== 串口连接 ====================

        private void RefreshPorts()
        {
            cmbPort.Items.Clear();
            foreach (var port in ModbusRtuClient.GetAvailablePorts())
                cmbPort.Items.Add(port);
            if (cmbPort.Items.Count > 0)
                cmbPort.SelectedIndex = 0;
        }

        private void BtnConnect_Click(object? sender, EventArgs e)
        {
            if (_isPolling)
            {
                StopPolling();
                StopBackgroundReconnect();
                _failCount = 0;
                _modbus.Close();
                btnConnect.Text = "连接";
                lblStatus.Text = "已断开";
                lblStatus.ForeColor = Color.Gray;
                lblConnIndicator.Text = "";
                lblConnIndicator.ForeColor = Color.Gray;
                cmbSlaveAddr.Enabled = true;
                return;
            }

            if (cmbPort.SelectedItem == null)
            {
                MessageBox.Show("请选择串口", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cmbBaudRate.SelectedItem == null)
            {
                MessageBox.Show("请选择波特率", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cmbSlaveAddr.SelectedItem == null)
            {
                MessageBox.Show("请选择从机地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                lblConnIndicator.Text = "连接中...";
                lblConnIndicator.ForeColor = Color.Orange;

                _modbus.BaudRate = int.Parse(cmbBaudRate.SelectedItem.ToString()!);
                _modbus.SlaveAddress = Convert.ToByte(cmbSlaveAddr.SelectedItem.ToString()!.Replace("0x", ""), 16);
                _modbus.Open(cmbPort.SelectedItem.ToString()!);
                _lastPortName = cmbPort.SelectedItem.ToString();

                btnConnect.Text = "断开";
                lblStatus.Text = $"已连接: {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps, 从机=0x{_modbus.SlaveAddress:X2}";
                lblStatus.ForeColor = Color.Green;
                lblConnIndicator.Text = "连接成功";
                lblConnIndicator.ForeColor = Color.Green;
                cmbSlaveAddr.Enabled = false;
                AppendLog("SYS", $"已连接 {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps");

                StartPolling();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lblConnIndicator.Text = "连接失败";
                lblConnIndicator.ForeColor = Color.Red;
                AppendLog("SYS", $"连接失败: {ex.Message}");
                _modbus.Close();
                cmbSlaveAddr.Enabled = true;
            }
        }

        // ==================== 轮询 ====================

        private void StartPolling()
        {
            _isPolling = true;
            int generation = Interlocked.Increment(ref _pollGeneration);

            _timeData.Clear();
            foreach (List<double> numericData in _numericCurveData.Values)
                numericData.Clear();
            foreach (List<ushort> registerData in _allBitRegisterData.Values)
                registerData.Clear();
            _nextCurvePruneAt = DateTime.MinValue;
            _automaticArchiveSegmentStart = DateTime.Now;
            _nextAutomaticArchiveAt = _automaticArchiveSegmentStart + AutomaticArchiveInterval;
            _nextAutomaticArchiveRetryAt = DateTime.MinValue;
            _lastSavedSampleTime = double.NegativeInfinity;
            _lastFaultSignature = string.Empty;
            _lastFaultCount = -1;
            _chartDataDirty = true;
            _pollTimer = new System.Windows.Forms.Timer { Interval = (int)nudInterval.Value };
            _pollTimer.Tick += async (s, e) => await PollDataAsync(generation);
            _pollTimer.Start();
            _ = PollDataAsync(generation);
        }

        private void StopPolling()
        {
            _isPolling = false;
            Interlocked.Increment(ref _pollGeneration);
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        /// <summary>计算第 attempt 次重连前的等待时间：1s、2s、4s… 封顶 30s。</summary>
        internal static int ComputeReconnectDelayMs(int attempt)
        {
            if (attempt <= 0)
                return ReconnectBaseDelayMs;
            double delay = ReconnectBaseDelayMs * Math.Pow(2, attempt - 1);
            return (int)Math.Min(delay, ReconnectMaxDelayMs);
        }

        private void StartBackgroundReconnect(int generation)
        {
            if (Interlocked.Exchange(ref _reconnectActive, 1) != 0)
                return; // 已有重连在跑

            CancellationTokenSource cts = new();
            _reconnectCts = cts;
            CancellationToken token = cts.Token;
            int attempt = 0;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested
                        && _isPolling
                        && generation == Volatile.Read(ref _pollGeneration))
                    {
                        int delayMs = ComputeReconnectDelayMs(attempt++);
                        string waitText = delayMs >= 1000
                            ? $"{delayMs / 1000.0:0.#} 秒后重试"
                            : $"{delayMs} 毫秒后重试";
                        UpdateReconnectStatus($"重连中... (第 {attempt} 次, {waitText})");
                        try { await Task.Delay(delayMs, token); }
                        catch (OperationCanceledException) { return; }

                        bool reconnected = false;
                        try
                        {
                            reconnected = await Task.Run(() => TryReconnect(token), token);
                        }
                        catch (OperationCanceledException) { return; }

                        if (!reconnected) continue;

                        // 成功：恢复轮询与 UI
                        if (InvokeRequired)
                        {
                            if (IsDisposed || Disposing) return;
                            await Task.Run(() => Invoke(() => OnReconnectSucceeded(generation)));
                        }
                        else
                        {
                            OnReconnectSucceeded(generation);
                        }
                        return;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnectActive, 0);
                    cts.Dispose();
                }
            }, CancellationToken.None);
        }

        private void OnReconnectSucceeded(int generation)
        {
            if (generation != Volatile.Read(ref _pollGeneration))
            {
                // 重连期间用户已手动断开，保持断开状态
                _modbus.Close();
                return;
            }
            UpdateReconnectStatus($"已连接: {_lastPortName} @ {_modbus.BaudRate}bps (重连恢复)");
            lblStatus.ForeColor = Color.Green;
            lblConnIndicator.Text = "连接成功";
            lblConnIndicator.ForeColor = Color.Green;
            if (_isPolling && _pollTimer != null) _pollTimer.Start();
        }

        private void StopBackgroundReconnect()
        {
            Interlocked.Exchange(ref _reconnectActive, 0);
            CancellationTokenSource? cts = Interlocked.Exchange(ref _reconnectCts, null);
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { } // 后台任务已自行释放
        }

        private bool TryReconnect(CancellationToken token)
        {
            if (string.IsNullOrEmpty(_lastPortName))
                return false;
            try
            {
                // 串口已消失（USB 拔出）时等待重新枚举，避免对不存在的口反复 Open
                if (!ModbusRtuClient.GetAvailablePorts().Contains(_lastPortName, StringComparer.OrdinalIgnoreCase))
                {
                    AppendLog("SYS", $"串口 {_lastPortName} 不存在，等待重新接入...");
                    return false;
                }
                token.ThrowIfCancellationRequested();
                _modbus.Close();
                _modbus.Open(_lastPortName);
                _modbus.ReadInputRegisters(30101, 6);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppendLog("SYS", $"重连失败: {ex.Message}");
                return false;
            }
        }

        private void UpdateReconnectStatus(string text)
        {
            if (InvokeRequired) { Invoke(() => UpdateReconnectStatus(text)); return; }
            lblStatus.Text = text;
            AppendLog("SYS", text);
        }

        private async Task PollDataAsync(int generation)
        {
            if (!_isPolling
                || generation != Volatile.Read(ref _pollGeneration)
                || !_modbus.IsConnected)
                return;
            if (Interlocked.Exchange(ref _pollInProgress, 1) != 0) return;

            try
            {
                _pollTimer?.Stop();

                Dictionary<ushort, ushort> values = await Task.Run(() => ReadPollRegisters(generation));
                if (generation != Volatile.Read(ref _pollGeneration))
                    return;
                DeriveModeRegisters(values);
                UpdateDisplay(values);

                SetLabelText(lblUpdateTime, $"更新: {DateTime.Now:HH:mm:ss.fff}");
                _failCount = 0;
            }
            catch (OperationCanceledException) when (generation != Volatile.Read(ref _pollGeneration))
            {
            }
            catch (Exception ex)
            {
                AppendLog("ERR", ex is TimeoutException ? "通信超时" : ex.Message);
                _failCount++;
                if (_failCount >= ReconnectThreshold)
                {
                    _failCount = 0;
                    _pollTimer?.Stop();
                    UpdateReconnectStatus($"连接异常，进入自动重连 ({(ex is TimeoutException ? "通信超时" : ex.Message)})");
                    StartBackgroundReconnect(generation);
                }
                else
                {
                    lblStatus.Text = ex is TimeoutException ? "通信超时..." : $"错误: {ex.Message}";
                    lblStatus.ForeColor = Color.Orange;
                }
            }
            finally
            {
                Volatile.Write(ref _pollInProgress, 0);
                // 后台重连进行中时不重启计时器，恢复连接后由重连循环统一拉起
                if (_isPolling
                    && generation == Volatile.Read(ref _pollGeneration)
                    && Volatile.Read(ref _reconnectActive) == 0
                    && !_writeInProgress
                    && _pollTimer != null)
                    _pollTimer.Start();
            }
        }

        private Dictionary<ushort, ushort> ReadPollRegisters(int generation)
        {
            Dictionary<ushort, ushort> values = ReadPollBlockSet(
                PollBlocks, generation, out int essentialBlocksRead, out bool mergedReadFailed);
            if (!mergedReadFailed && essentialBlocksRead > 0)
                return values;

            // 设备不支持合并读取时回退，保证老型号仍可使用。
            AppendLog("SYS", "连续寄存器读取失败，回退到兼容分块模式");
            values = ReadPollBlockSet(LegacyPollBlocks, generation, out essentialBlocksRead, out _);
            if (essentialBlocksRead == 0)
                throw new TimeoutException("核心状态寄存器均未响应");
            return values;
        }

        private Dictionary<ushort, ushort> ReadPollBlockSet(
            IReadOnlyList<(byte FunctionCode, ushort StartAddress, ushort Quantity)> blocks,
            int generation,
            out int essentialBlocksRead,
            out bool hadFailure)
        {
            var values = new Dictionary<ushort, ushort>();
            essentialBlocksRead = 0;
            hadFailure = false;
            foreach (var block in blocks)
            {
                if (generation != Volatile.Read(ref _pollGeneration))
                    throw new OperationCanceledException();
                try
                {
                    ushort[] blockValues = block.FunctionCode == 0x04
                        ? _modbus.ReadInputRegisters(block.StartAddress, block.Quantity)
                        : _modbus.ReadHoldingRegisters(block.StartAddress, block.Quantity);
                    for (int i = 0; i < blockValues.Length; i++)
                        values[(ushort)(block.StartAddress + i)] = blockValues[i];
                    if (block.StartAddress is 30101 or 30201 or 30231)
                        essentialBlocksRead++;
                }
                catch (Exception ex)
                {
                    hadFailure = true;
                    AppendLog("ERR",
                        $"读取 {block.StartAddress}/{block.Quantity} 失败: {ex.Message}");
                }
            }
            return values;
        }

        // ==================== 数据更新 ====================

        private void UpdateDisplay(Dictionary<ushort, ushort> values)
        {
            if (InvokeRequired) { Invoke(() => UpdateDisplay(values)); return; }

            ushort Get(ushort address) => values.TryGetValue(address, out ushort value) ? value : (ushort)0;
            bool Has(ushort address) => values.ContainsKey(address);
            bool hasCompleteFaultData = Enumerable.Range(30101, 5).All(address => Has((ushort)address));

            ushort[] faultRegs = { Get(30101), Get(30102), Get(30103), Get(30104), Get(30105) };
            ushort status = Get(30106);
            ushort deviceStatus = Get(30201);
            ushort workMode = (ushort)((status >> 3) & 0x07);
            bool isOn = (status & 0x0001) != 0;
            bool defrostRequested = (status & 0x1000) != 0;
            bool defrostRunning = (deviceStatus & 0x1000) != 0;

            // 原地更新表格值（不再重建）
            void SetVal(ushort addr, string value)
            {
                SetRegisterDisplayValue(addr, value);
            }

            // 先写入所有已读取寄存器的原始值，再覆盖需要换算或解释的字段。
            foreach (var pair in values)
                SetVal(pair.Key, FormatParameterValue(pair.Key, pair.Value));

            if (Has(30101)) UpdateFaultBitRows(30101, Get(30101), RegisterMap.FaultReg1Bits);
            if (Has(30102)) UpdateFaultBitRows(30102, Get(30102), RegisterMap.FaultReg2Bits);
            if (Has(30103)) UpdateFaultBitRows(30103, Get(30103), RegisterMap.FaultReg3Bits);
            if (Has(30104)) UpdateFaultBitRows(30104, Get(30104), RegisterMap.FaultReg4Bits);
            if (Has(30105)) UpdateFaultBitRows(30105, Get(30105), RegisterMap.FaultReg5Bits);
            if (Has(30106)) UpdateStatusBitRows(30106, Get(30106), RegisterMap.StatusWordBits);
            if (Has(30201)) UpdateStatusBitRows(30201, Get(30201), RegisterMap.DeviceStatusBits);
            if (Has(30229)) UpdateStatusBitRows(30229, Get(30229), RegisterMap.DipSwitchBits);

            if (Has(30001)) SetVal(30001, FormatRegisterWords(values, 30001, 2));
            if (Has(30003)) SetVal(30003, FormatRegisterWords(values, 30003, 2));
            if (Has(30005)) SetVal(30005, DecodeAscii(values, 30005, 4));
            if (Has(30009)) SetVal(30009, FormatRegisterWords(values, 30009, 2));
            if (Has(30011)) SetVal(30011, FormatRegisterWords(values, 30011, 2));

            if (Has(30106))
                SetVal(30106, $"0x{status:X4} / {(isOn ? "开机" : "关机")} / {WorkModeMapGet(workMode)}");
            if (Has(30201))
                SetVal(30201, $"0x{deviceStatus:X4}");
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (HasNumericCurveValue(definition, values))
                {
                    double value = DecodeNumericCurveValue(definition, values);
                    SetVal(definition.Address, FormatNumericCurveValue(definition, value));
                }
            }

            // 故障报警寄存器 - 显示原始十六进制值
            for (int i = 0; i < faultRegs.Length; i++)
            {
                ushort address = (ushort)(30101 + i);
                if (Has(address))
                    SetVal(address, $"0x{faultRegs[i]:X4}");
            }

            if (Has(30106))
            {
                string silentText = Has(40002) ? SilentMapGet(Get(40002)) : "数据缺失";
                string defrostText = Has(30201)
                    ? (defrostRequested || defrostRunning ? "是" : "否")
                    : "数据缺失";
                SetLabelText(lblWorkMode, $"工作状态: 电源 {(isOn ? "开机" : "关机")} | 静音 {silentText} | 除霜 {defrostText}");
                SetLabelText(lblRuntimeMode, $"运行模式1: {RuntimeModeText(values)}", Color.Red);
                SetLabelText(lblRuntimeMode2, $"运行模式2: {RuntimeMode2Text(values)}", Color.Red);
                SetLabelText(lblSetMode, $"设置模式: {SetModeText(values)}", Color.Black);
            }
            else
            {
                SetLabelText(lblWorkMode, "工作状态: 数据不完整");
                SetLabelText(lblRuntimeMode, "运行模式1: --", Color.Red);
                SetLabelText(lblRuntimeMode2, "运行模式2: --", Color.Red);
                SetLabelText(lblSetMode, "设置模式: --", Color.Black);
            }

            int faultCount = -1;
            if (hasCompleteFaultData)
            {
                string faultSignature = string.Join(",", faultRegs);
                if (!string.Equals(faultSignature, _lastFaultSignature, StringComparison.Ordinal))
                {
                    lstFaults.BeginUpdate();
                    try
                    {
                        lstFaults.Items.Clear();
                        CheckFaultBits(faultRegs[0], RegisterMap.FaultReg1Bits, "30101", 1);
                        CheckFaultBits(faultRegs[1], RegisterMap.FaultReg2Bits, "30102", 17);
                        CheckFaultBits(faultRegs[2], RegisterMap.FaultReg3Bits, "30103", 33);
                        CheckFaultBits(faultRegs[3], RegisterMap.FaultReg4Bits, "30104", 49);
                        CheckFaultBits(faultRegs[4], RegisterMap.FaultReg5Bits, "30105", 65);
                    }
                    finally
                    {
                        lstFaults.EndUpdate();
                    }
                    _lastFaultSignature = faultSignature;
                    _lastFaultCount = lstFaults.Items.Count;
                }
                faultCount = _lastFaultCount;
                SetLabelText(lblFaultCode, faultCount == 0 ? "故障汇总: 无故障" : $"故障汇总: {faultCount} 项",
                    faultCount == 0 ? Color.Green : Color.Red);
            }
            else
            {
                _lastFaultSignature = string.Empty;
                _lastFaultCount = -1;
                SetLabelText(lblFaultCode, "故障汇总: 数据不完整", Color.Orange);
            }

            if (faultCount > 0)
            { SetLabelText(lblStatus, "⚠ 检测到故障报警！", Color.Red); }
            else if (_isPolling)
            { SetLabelText(lblStatus, $"已连接: {cmbPort.SelectedItem} @ {_modbus.BaudRate}bps", Color.Green); }

            // 曲线
            DateTime sampleTime = DateTime.Now;
            if (!CanRecordCurveSample(values))
                return;

            _timeData.Add(sampleTime.ToOADate());
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (!HasNumericCurveValue(definition, values))
                    return; // 寄存器块读取失败时整条样本不落库，保持各序列索引对齐
                _numericCurveData[definition.Address].Add(DecodeNumericCurveValue(definition, values));
            }

            foreach (ushort address in BitCurveRegisterAddresses)
            {
                if (!values.TryGetValue(address, out ushort bitValue))
                    return;
                _allBitRegisterData[address].Add(bitValue);
            }

            PruneExpiredCurveData(sampleTime);
            if (_activeAutomaticArchiveTask == null || _activeAutomaticArchiveTask.IsCompleted)
                _activeAutomaticArchiveTask = TryAutomaticArchiveAsync(sampleTime);
            _chartDataDirty = true;
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
            UpdateChartRenderRange(stateFormsPlot);
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
            try { AutoScaleChart(); } catch { }
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

                ScottPlot.WinForms.FormsPlot existingPlot = IsStatusCurve(definition)
                    ? stateFormsPlot
                    : bitFormsPlot;
                existingPlot.Plot.Remove(existingCurve);
                _bitCurvePlottables.Remove(key);
                return;
            }
            if (!visible)
                return;

            ScottPlot.WinForms.FormsPlot targetPlot = IsStatusCurve(definition)
                ? stateFormsPlot
                : bitFormsPlot;

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

        private void AutoScaleBitChart(ScottPlot.WinForms.FormsPlot plot)
        {
            var (xMin, xMax) = ComputeXAxisLimits();
            UpdateChartRenderRange(plot, xMin, xMax);
            plot.Plot.Axes.AutoScale();
            if (xMax > xMin)
                plot.Plot.Axes.SetLimitsX(xMin, xMax);
            // 状态页含枚举曲线，Y 轴范围按可见的运行模式1/2枚举曲线量程取最大；故障页仍用 0/1
            double enumMaxY = -1;
            if (plot == stateFormsPlot)
            {
                if (_bitCurvePlottables.TryGetValue((RUNTIME_MODE_CURVE_ADDR, 0), out var runtimeCurve1)
                    && runtimeCurve1.IsVisible)
                    enumMaxY = Math.Max(enumMaxY, RUNTIME_MODE1_NONE);
                if (_bitCurvePlottables.TryGetValue((RUNTIME_MODE2_CURVE_ADDR, 0), out var runtimeCurve2)
                    && runtimeCurve2.IsVisible)
                    enumMaxY = Math.Max(enumMaxY, RUNTIME_MODE2_COMPRESSOR_OFF);
            }
            if (enumMaxY >= 0)
                plot.Plot.Axes.SetLimitsY(-0.5, enumMaxY + 0.5);
            else
                plot.Plot.Axes.SetLimitsY(-0.15, 1.15);
            plot.Refresh();
        }

        private void BackToBitNow()
        {
            _followBitCurrentTime = true;
            try { AutoScaleBitChart(bitFormsPlot); } catch { }
        }

        private void BackToStateNow()
        {
            _followStateCurrentTime = true;
            try { AutoScaleBitChart(stateFormsPlot); } catch { }
        }

        private void FormsPlot_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_timeData.Count == 0)
            {
                _chartToolTip.Hide(formsPlot);
                return;
            }

            NumericCurveDefinition? selectedDefinition = null;
            ScottPlot.DataPoint selectedPoint = default;
            float selectedDistance = float.PositiveInfinity;
            var mousePixel = new ScottPlot.Pixel(e.X, e.Y);
            foreach (NumericCurveDefinition definition in NumericCurveDefinitions)
            {
                if (!_numericCurvePlottables.TryGetValue(definition.Address, out ScottPlot.IPlottable? curve)
                    || !curve.IsVisible
                    || curve is not ScottPlot.IGetNearest nearest)
                    continue;

                ScottPlot.Coordinates mouseCoordinates = formsPlot.Plot.GetCoordinates(
                    e.X, e.Y, curve.Axes.XAxis, curve.Axes.YAxis);
                ScottPlot.DataPoint point = nearest.GetNearest(
                    mouseCoordinates, formsPlot.Plot.LastRender, CurveClickDistancePixels);
                if (!point.IsReal)
                    continue;

                float distance = formsPlot.Plot
                    .GetPixel(point.Coordinates, curve.Axes.XAxis, curve.Axes.YAxis)
                    .DistanceFrom(mousePixel);
                if (distance < selectedDistance)
                {
                    selectedDefinition = definition;
                    selectedPoint = point;
                    selectedDistance = distance;
                }
            }

            if (selectedDefinition == null
                || selectedPoint.Index < 0
                || selectedPoint.Index >= _timeData.Count)
            {
                _chartToolTip.Hide(formsPlot);
                return;
            }

            string value = FormatNumericCurveValue(selectedDefinition, selectedPoint.Y);
            string time = DateTime.FromOADate(_timeData[selectedPoint.Index]).ToString("HH:mm:ss.fff");
            string unitSuffix = IsDurationAddress(selectedDefinition.Address)
                ? string.Empty
                : $" {selectedDefinition.Unit}";
            ShowCurveToolTip(
                formsPlot,
                e,
                $"时间：{time}\n{selectedDefinition.Name}：{value}{unitSuffix}");
        }

        private void BitFormsPlot_MouseMove(object? sender, MouseEventArgs e)
        {
            if (sender is not ScottPlot.WinForms.FormsPlot plot)
                return;

            if (_timeData.Count == 0)
            {
                _chartToolTip.Hide(plot);
                return;
            }

            BitCurveDefinition? selectedDefinition = null;
            ScottPlot.DataPoint selectedPoint = default;
            float selectedDistance = float.PositiveInfinity;
            var mousePixel = new ScottPlot.Pixel(e.X, e.Y);
            IReadOnlyList<BitCurveDefinition> definitions = plot == stateFormsPlot
                ? StatusCurveDefinitions
                : FaultCurveDefinitions;
            foreach (BitCurveDefinition definition in definitions)
            {
                var key = (definition.Address, definition.Bit);
                if (!_bitCurvePlottables.TryGetValue(key, out ScottPlot.IPlottable? curve)
                    || !curve.IsVisible
                    || curve is not ScottPlot.IGetNearest nearest)
                    continue;

                ScottPlot.Coordinates mouseCoordinates = plot.Plot.GetCoordinates(
                    e.X, e.Y, curve.Axes.XAxis, curve.Axes.YAxis);
                ScottPlot.DataPoint point = nearest.GetNearest(
                    mouseCoordinates, plot.Plot.LastRender, CurveClickDistancePixels);
                if (!point.IsReal)
                    continue;

                float distance = plot.Plot
                    .GetPixel(point.Coordinates, curve.Axes.XAxis, curve.Axes.YAxis)
                    .DistanceFrom(mousePixel);
                if (distance < selectedDistance)
                {
                    selectedDefinition = definition;
                    selectedPoint = point;
                    selectedDistance = distance;
                }
            }

            if (selectedDefinition == null
                || selectedPoint.Index < 0
                || selectedPoint.Index >= _timeData.Count)
            {
                _chartToolTip.Hide(plot);
                return;
            }

            string time = DateTime.FromOADate(_timeData[selectedPoint.Index]).ToString("HH:mm:ss.fff");
            string text;
            // 运行模式1/2虚拟曲线（枚举阶梯线）走枚举文本分支；其余状态曲线仍按位解读
            if (plot == stateFormsPlot
                && selectedDefinition.Address is RUNTIME_MODE_CURVE_ADDR or RUNTIME_MODE2_CURVE_ADDR
                && selectedPoint.Y is >= 0 and <= 7)
            {
                ushort val = (ushort)selectedPoint.Y;
                var meanings = selectedDefinition.Address == RUNTIME_MODE_CURVE_ADDR
                    ? RuntimeModeMeanings
                    : RuntimeMode2Meanings;
                string modeName = selectedDefinition.Address == RUNTIME_MODE_CURVE_ADDR
                    ? "运行模式1"
                    : "运行模式2";
                text = $"时间：{time}\n{modeName}：{val} / {(meanings.TryGetValue(val, out var m) ? m : val.ToString())}";
            }
            else
            {
                int value = selectedPoint.Y >= 0.5 ? 1 : 0;
                string stateText = value == 1 ? selectedDefinition.OneText : selectedDefinition.ZeroText;
                text = $"时间：{time}\n{selectedDefinition.Name}：{value} / {stateText}";
            }
            ShowCurveToolTip(plot, e, text);
        }

        private void ShowCurveToolTip(Control chartControl, MouseEventArgs e, string text)
        {
            int tooltipX = e.X + 16;
            int tooltipY = e.Y + 20;
            if (tooltipX > chartControl.ClientSize.Width - 260)
                tooltipX = Math.Max(4, e.X - 255);
            if (tooltipY > chartControl.ClientSize.Height - 80)
                tooltipY = Math.Max(4, e.Y - 75);
            _chartToolTip.Show(text, chartControl, tooltipX, tooltipY, 30000);
        }

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

        // ==================== 辅助 ====================

        private static void SetLabelText(Label label, string text, Color? foreColor = null)
        {
            if (!string.Equals(label.Text, text, StringComparison.Ordinal))
                label.Text = text;
            if (foreColor.HasValue && label.ForeColor != foreColor.Value)
                label.ForeColor = foreColor.Value;
        }

        private void SetRegisterDisplayValue(ushort address, string value)
        {
            if (!_rowMap.TryGetValue(address, out int rowIndex) || rowIndex >= dgvData.Rows.Count)
                return;

            DataGridViewRow row = dgvData.Rows[rowIndex];
            DataGridViewCell valueCell = row.Cells["Value"];
            if (!string.Equals(valueCell.Value?.ToString(), value, StringComparison.Ordinal))
                valueCell.Value = value;
            // 仅温度字段执行超限高亮，避免电压和功率等正常值被误判。
            bool isTemperature = row.Cells["Unit"].Value?.ToString() == "℃";
            Color color = isTemperature && double.TryParse(value, out double numericValue) && numericValue > 80
                ? Color.Red
                : Color.Black;
            if (row.DefaultCellStyle.ForeColor != color)
                row.DefaultCellStyle.ForeColor = color;
        }

        private static void SetBitRowColor(DataGridViewRow row, bool nonZero)
        {
            Color color = nonZero ? Color.Red : Color.Black;
            if (row.DefaultCellStyle.ForeColor != color)
                row.DefaultCellStyle.ForeColor = color;
        }

        private void UpdateFaultBitRows(ushort address, ushort rawValue, Dictionary<int, string> definitions)
        {
            foreach (var definition in definitions)
            {
                if (!_bitRowMap.TryGetValue((address, definition.Key), out int rowIndex))
                    continue;

                bool active = (rawValue & (1 << definition.Key)) != 0;
                DataGridViewRow row = dgvData.Rows[rowIndex];
                string value = active ? "1 / 触发" : "0 / 正常";
                if (!string.Equals(row.Cells["Value"].Value?.ToString(), value, StringComparison.Ordinal))
                    row.Cells["Value"].Value = value;
                SetBitRowColor(row, active);
            }
        }

        private void UpdateStatusBitRows(ushort address, ushort rawValue, Dictionary<int, BitDefinition> definitions)
        {
            foreach (var definition in definitions)
            {
                if (!_bitRowMap.TryGetValue((address, definition.Key), out int rowIndex))
                    continue;

                bool enabled = (rawValue & (1 << definition.Key)) != 0;
                string state = enabled ? definition.Value.OneText : definition.Value.ZeroText;
                DataGridViewRow row = dgvData.Rows[rowIndex];
                string value = $"{(enabled ? 1 : 0)} / {state}";
                if (!string.Equals(row.Cells["Value"].Value?.ToString(), value, StringComparison.Ordinal))
                    row.Cells["Value"].Value = value;
                SetBitRowColor(row, enabled);
            }
        }

        private void AddRow(string name, string value, string unit, string addr)
        {
            int idx = dgvData.Rows.Add(name, value, unit, addr);
            if (double.TryParse(value, out double v) && name.Contains("温度") && v > 80)
                dgvData.Rows[idx].DefaultCellStyle.ForeColor = Color.Red;
        }

        private void CheckFaultBits(ushort reg, Dictionary<int, string> defs, string regAddr, int firstFaultCode)
        {
            for (int b = 0; b < 16; b++)
            {
                if ((reg & (1 << b)) != 0)
                {
                    string name = defs.TryGetValue(b, out string? definedName) ? definedName : "未定义故障位";
                    int faultCode = firstFaultCode + b;
                    if (RegisterMap.FaultCodeMap.ContainsKey(faultCode))
                    {
                        string reset = RegisterMap.FaultResetMethodMap.TryGetValue(faultCode, out string? method)
                            ? method
                            : "清除方式未定义";
                        lstFaults.Items.Add($"[{regAddr}] BIT{b} / 故障码{faultCode}: {name}（{reset}）");
                    }
                    else
                    {
                        lstFaults.Items.Add($"[{regAddr}] BIT{b}: {name}");
                    }
                }
            }
        }

        private static string FormatRegisterWords(Dictionary<ushort, ushort> values, ushort startAddress, int count)
        {
            return string.Join(" ", Enumerable.Range(0, count)
                .Select(i => values.TryGetValue((ushort)(startAddress + i), out ushort value) ? $"{value:X4}" : "----"));
        }

        private static string DecodeAscii(Dictionary<ushort, ushort> values, ushort startAddress, int count)
        {
            var bytes = new List<byte>(count * 2);
            for (int i = 0; i < count; i++)
            {
                ushort value = values.TryGetValue((ushort)(startAddress + i), out ushort word) ? word : (ushort)0;
                bytes.Add((byte)(value >> 8));
                bytes.Add((byte)value);
            }
            return System.Text.Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\0', ' ');
        }

        private static string WorkModeMapGet(ushort m) => RegisterMap.WorkModeMap.TryGetValue(m, out var v) ? v : $"未知({m})";

        // ---------------- 虚拟状态曲线：运行模式1 / 运行模式2 ----------------
        // 这两个地址是软件内部编号，设备上无对应寄存器，不参与 Modbus 轮询，
        // 仅在轮询结果分发时由真实寄存器派生（见 DeriveModeRegisters），用于状态页曲线与导出。
        // 运行模式1（30990）由 30106 bit3~5 派生，枚举：0=数据缺失，1=制冷待机，2=制冷报警停机，
        // 3=制热待机，4=制热报警停机，5=除霜中，6=压缩机预热，7=--
        // 运行模式2（30991）由 30106 bit3~5 + bit6 派生，枚举：0=数据缺失，1=制冷，2=制冷+热水，
        // 3=制热，4=制热+热水，5=热水，6=压机未运行
        internal const ushort RUNTIME_MODE_CURVE_ADDR = 30990;   // 运行模式1（30106 bit3~5 派生）
        internal const ushort RUNTIME_MODE2_CURVE_ADDR = 30991;  // 运行模式2（30106 bit3~5 + bit6 派生）

        // ---- 运行模式1 枚举 ----
        internal const ushort RUNTIME_MODE_DATA_MISSING = 0;
        internal const ushort RUNTIME_MODE1_COOLING_STANDBY = 1;        // 制冷待机
        internal const ushort RUNTIME_MODE1_COOLING_ALARM_STOP = 2;     // 制冷报警停机
        internal const ushort RUNTIME_MODE1_HEATING_STANDBY = 3;        // 制热待机
        internal const ushort RUNTIME_MODE1_HEATING_ALARM_STOP = 4;     // 制热报警停机
        internal const ushort RUNTIME_MODE1_DEFROSTING = 5;             // 除霜中
        internal const ushort RUNTIME_MODE1_PREHEATING = 6;             // 压缩机预热
        internal const ushort RUNTIME_MODE1_NONE = 7;                   // --（000B / 011B）

        // ---- 运行模式2 枚举 ----
        internal const ushort RUNTIME_MODE2_COOLING = 1;                // 制冷
        internal const ushort RUNTIME_MODE2_COOLING_HOT_WATER = 2;      // 制冷+热水
        internal const ushort RUNTIME_MODE2_HEATING = 3;                // 制热
        internal const ushort RUNTIME_MODE2_HEATING_HOT_WATER = 4;      // 制热+热水
        internal const ushort RUNTIME_MODE2_HOT_WATER = 5;              // 热水
        internal const ushort RUNTIME_MODE2_COMPRESSOR_OFF = 6;         // 压机未运行

        internal static readonly Dictionary<ushort, string> RuntimeModeMeanings = new()
        {
            [RUNTIME_MODE_DATA_MISSING] = "数据缺失",
            [RUNTIME_MODE1_COOLING_STANDBY] = "制冷待机",
            [RUNTIME_MODE1_COOLING_ALARM_STOP] = "制冷报警停机",
            [RUNTIME_MODE1_HEATING_STANDBY] = "制热待机",
            [RUNTIME_MODE1_HEATING_ALARM_STOP] = "制热报警停机",
            [RUNTIME_MODE1_DEFROSTING] = "除霜中",
            [RUNTIME_MODE1_PREHEATING] = "压缩机预热",
            [RUNTIME_MODE1_NONE] = " -- ",
        };

        internal static readonly Dictionary<ushort, string> RuntimeMode2Meanings = new()
        {
            [RUNTIME_MODE_DATA_MISSING] = "数据缺失",
            [RUNTIME_MODE2_COOLING] = "制冷",
            [RUNTIME_MODE2_COOLING_HOT_WATER] = "制冷+热水",
            [RUNTIME_MODE2_HEATING] = "制热",
            [RUNTIME_MODE2_HEATING_HOT_WATER] = "制热+热水",
            [RUNTIME_MODE2_HOT_WATER] = "热水",
            [RUNTIME_MODE2_COMPRESSOR_OFF] = "压机未运行",
        };

        /// <summary>
        /// 由 30106 实际状态计算运行模式1枚举：bit3~5（001=制冷待机，010=制冷报警停机，
        /// 100=制热待机，101=制热报警停机，110=除霜中，111=压缩机预热，000/011=--）。
        /// 30106 缺失时返回 0（数据缺失）。
        /// </summary>
        internal static ushort ComputeRuntimeModeEnum(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.STATUS_WORD_ADDR, out ushort status))
                return RUNTIME_MODE_DATA_MISSING;

            return ((status >> 3) & 0x07) switch
            {
                0b001 => RUNTIME_MODE1_COOLING_STANDBY,
                0b010 => RUNTIME_MODE1_COOLING_ALARM_STOP,
                0b100 => RUNTIME_MODE1_HEATING_STANDBY,
                0b101 => RUNTIME_MODE1_HEATING_ALARM_STOP,
                0b110 => RUNTIME_MODE1_DEFROSTING,
                0b111 => RUNTIME_MODE1_PREHEATING,
                _ => RUNTIME_MODE1_NONE // 000B / 011B
            };
        }

        /// <summary>
        /// 由 30106 实际状态计算运行模式2枚举：bit3~5 与 bit6 组合。
        /// 000+bit6=0→制冷；000+bit6=1→制冷+热水；011+bit6=0→制热；011+bit6=1→制热+热水；
        /// 其他 bit3~5+bit6=1→热水；其他 bit3~5+bit6=0→压机未运行。30106 缺失时返回 0（数据缺失）。
        /// </summary>
        internal static ushort ComputeRuntimeMode2Enum(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.STATUS_WORD_ADDR, out ushort status))
                return RUNTIME_MODE_DATA_MISSING;

            ushort baseBits = (ushort)((status >> 3) & 0x07);
            // bit6 生活热水运行状态；30223 电动三通球阀1 位置（≠0=通热水，热水相关模式才显示）
            bool hotWater = (status & 0x0040) != 0
                && values.TryGetValue(RegisterMap.THREE_WAY_VALVE1_ADDR, out ushort valve1)
                && valve1 != 0;

            return (baseBits, hotWater) switch
            {
                (0b000, false) => RUNTIME_MODE2_COOLING,
                (0b000, true) => RUNTIME_MODE2_COOLING_HOT_WATER,
                (0b011, false) => RUNTIME_MODE2_HEATING,
                (0b011, true) => RUNTIME_MODE2_HEATING_HOT_WATER,
                (_, true) => RUNTIME_MODE2_HOT_WATER,
                _ => RUNTIME_MODE2_COMPRESSOR_OFF
            };
        }

        /// <summary>轮询后向 values 注入虚拟模式寄存器（30990/30991），供状态页曲线与导出使用。</summary>
        internal static void DeriveModeRegisters(Dictionary<ushort, ushort> values)
        {
            values[RUNTIME_MODE_CURVE_ADDR] = ComputeRuntimeModeEnum(values);
            values[RUNTIME_MODE2_CURVE_ADDR] = ComputeRuntimeMode2Enum(values);
        }

        private static string RuntimeModeText(IReadOnlyDictionary<ushort, ushort> values) =>
            RuntimeModeMeanings.TryGetValue(ComputeRuntimeModeEnum(values), out var runtimeText) ? runtimeText : "数据缺失";

        private static string RuntimeMode2Text(IReadOnlyDictionary<ushort, ushort> values) =>
            RuntimeMode2Meanings.TryGetValue(ComputeRuntimeMode2Enum(values), out var runtimeText) ? runtimeText : "数据缺失";

        /// <summary>
        /// 顶部状态栏"设置模式"文本：由 40001（开关机）、40201（1=制冷，2=制热）与 40202（生活热水启用）组合。
        /// 40001=1 时判断 40201 显示制冷/制热；40001=0 时不判断 40201，不显示制冷/制热。
        /// 40202=1 时显示热水；40001 和 40202 都是 0 时才显示"关机"，40001=0 且 40202=1 显示"热水"。
        /// </summary>
        internal static string SetModeText(IReadOnlyDictionary<ushort, ushort> values)
        {
            if (!values.TryGetValue(RegisterMap.ON_OFF_ADDR, out ushort onOff) ||
                !values.TryGetValue(RegisterMap.SET_WORK_MODE_ADDR, out ushort mode) ||
                !values.TryGetValue(RegisterMap.HOT_WATER_ENABLE_ADDR, out ushort hotWaterRaw))
                return "数据缺失";

            // 40001=0：不判断 40201，不显示制冷/制热；40202=1 显示"热水"，40202=0 显示"关机"
            if (onOff == 0)
                return hotWaterRaw != 0 ? "热水" : "关机";

            string baseMode = mode switch
            {
                1 => "制冷",
                2 => "制热",
                _ => "数据缺失"
            };
            if (baseMode == "数据缺失")
                return baseMode;

            return hotWaterRaw != 0 ? $"{baseMode}+热水" : baseMode;
        }

        private static string SilentMapGet(ushort m) => RegisterMap.SilentModeMap.TryGetValue(m, out var v) ? v : $"未知({m})";

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopPolling();
            StopBackgroundReconnect();
            if (_logFlushTimer != null)
            {
                _logFlushTimer.Stop();
                _logFlushTimer.Dispose();
                _logFlushTimer = null;
            }
            if (_chartRenderTimer != null)
            {
                _chartRenderTimer.Stop();
                _chartRenderTimer.Dispose();
                _chartRenderTimer = null;
            }
            _chartToolTip.Dispose();
            _toolTip.Dispose();
            _modbus.Dispose();
            base.OnFormClosed(e);
        }
    }
}
