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
        // 改动1：实时曲线单页化——参数曲线（上）与状态/故障BIT曲线（下）上下分区。
        private ScottPlot.WinForms.FormsPlot bitFormsPlot = null!;
        private SplitContainer chartSplit = null!;
        private Button btnToggleBitPanel = null!;
        private TreeView numericSelector = null!;
        private TreeView bitSelector = null!;
        private readonly Dictionary<ushort, ScottPlot.IPlottable> _numericCurvePlottables = new();
        private readonly Dictionary<ushort, List<double>> _numericCurveData = new();
        private readonly Dictionary<(ushort Address, int Bit), ScottPlot.IPlottable> _bitCurvePlottables = new();
        // 每次采样保存全部8个BIT寄存器的完整16位原始值，与曲线是否勾选无关。
        private readonly Dictionary<ushort, List<ushort>> _allBitRegisterData = new();
        // 上下分区 X 轴联动：统一跟随标志 + 防止同步递归。
        private bool _followCurrentTime = true;
        private bool _syncingChartXAxes;
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
        private readonly ChartToolTip _chartToolTip = new();

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
        internal sealed class EnumScatterSource : ScatterSourceBase
        {
            private readonly List<ushort> _values;
            private readonly Dictionary<ushort, string> _meanings;
            private readonly double _maxY;

            internal EnumScatterSource(List<double> times, List<ushort> values, Dictionary<ushort, string> meanings, double maxY)
                : base(times)
            {
                _values = values;
                _meanings = meanings;
                _maxY = maxY;
            }

            protected override int ValueCount => _values.Count;

            protected override ScottPlot.Coordinates GetPoint(int index) =>
                new(Times[index], _values[index]);

            public override ScottPlot.CoordinateRange GetLimitsY() =>
                new(-0.5, _maxY + 0.5);

            internal string GetMeaning(ushort val) =>
                _meanings.TryGetValue(val, out var m) ? m : val.ToString();
        }

        private sealed class BitScatterSource : ScatterSourceBase
        {
            private readonly List<ushort> _registerValues;
            private readonly int _bit;

            internal BitScatterSource(List<double> times, List<ushort> registerValues, int bit)
                : base(times)
            {
                _registerValues = registerValues;
                _bit = bit;
            }

            protected override int ValueCount => _registerValues.Count;

            protected override ScottPlot.Coordinates GetPoint(int index) =>
                new(Times[index], (_registerValues[index] & (1 << _bit)) == 0 ? 0.0 : 1.0);

            public override ScottPlot.CoordinateRange GetLimitsY() =>
                new(0, 1);
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

            // 右下：实时曲线单页——参数曲线（上分区）与状态/故障BIT曲线（下分区，可隐藏）。
            var chartHost = new Panel { Dock = DockStyle.Fill };
            chartSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6
            };

            // 上分区：温度、频率、功率等连续数值。
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
            formsPlot.MouseLeave += (s, e) => HideChartToolTip(formsPlot);
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
                Width = 220,
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
                // 上下两个分区的曲线选择器同步显示/隐藏。
                bool showSelector = ApplyTreeSelectorToggle(numericSelector, bitSelector);
                btnToggleCurveSelector.Text = showSelector ? "隐藏曲线选择" : "显示曲线选择";
            };
            _toolTip.SetToolTip(btnToggleCurveSelector, "切换参数曲线/BIT曲线右侧选择区域");
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

            // 上分区顶部工具栏浮动"显示/隐藏BIT曲线"按钮。
            btnToggleBitPanel = new Button
            {
                Text = "隐藏BIT曲线",
                FlatStyle = FlatStyle.Flat,
                Size = new Size(110, 28),
                Font = new Font("Microsoft YaHei", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            btnToggleBitPanel.Click += (s, e) =>
            {
                bool show = chartSplit.Panel2Collapsed;
                chartSplit.Panel2Collapsed = !show;
                btnToggleBitPanel.Text = show ? "隐藏BIT曲线" : "显示BIT曲线";
            };
            _toolTip.SetToolTip(btnToggleBitPanel, "切换下方状态/故障BIT曲线分区显示");
            curveToolbar.Controls.Add(btnToggleBitPanel);

            var numericPanel = new Panel { Dock = DockStyle.Fill };
            numericPanel.Controls.Add(numericChartContent);
            numericPanel.Controls.Add(curveToolbar);
            chartSplit.Panel1.Controls.Add(numericPanel);

            // BIT 历史数据由故障页和状态页共享，但每条曲线只显示在所属页面。
            foreach (ushort address in BitCurveRegisterAddresses)
                _allBitRegisterData[address] = new List<ushort>();

            // 下分区：状态+故障 BIT 曲线共用一个图，右侧选择树保留两组勾选（状态/故障两个根组）。
            bitFormsPlot = new ScottPlot.WinForms.FormsPlot { Dock = DockStyle.Fill };
            bitFormsPlot.MouseDown += (s, e) =>
            {
                BeginChartInteraction();
                BeginChartPointer(bitFormsPlot, e.Location);
            };
            bitFormsPlot.MouseWheel += (s, e) =>
            {
                BeginChartInteraction();
                _followCurrentTime = false;
                EndChartInteraction(bitFormsPlot);
            };
            bitFormsPlot.MouseUp += (s, e) => EndChartInteraction(bitFormsPlot);
            bitFormsPlot.MouseCaptureChanged += (s, e) =>
            {
                if (!bitFormsPlot.Capture && Control.MouseButtons == MouseButtons.None)
                    EndChartInteraction(bitFormsPlot);
            };
            bitFormsPlot.MouseMove += (s, e) =>
            {
                UpdateChartPointerDrag(bitFormsPlot, e, () => _followCurrentTime = false);
                BitFormsPlot_MouseMove(s, e);
            };
            bitFormsPlot.MouseLeave += (s, e) => HideChartToolTip(bitFormsPlot);

            var dtBitTickGenerator = new ScottPlot.TickGenerators.DateTimeAutomatic();
            dtBitTickGenerator.LabelFormatter = dt => dt.ToString("HH:mm:ss");
            bitFormsPlot.Plot.Axes.Bottom.TickGenerator = dtBitTickGenerator;
            bitFormsPlot.Plot.Legend.IsVisible = false;
            // X 轴联动要求两图数据区水平对齐（同一时间在垂直同一条线上）：
            // 自定义布局引擎取参数图数据区左右边界，上下边界保持自身默认，
            // 底轴线固定贴本窗口底部（MatchedDataRect 会把垂直范围一并复制导致底轴线被裁掉）。
            bitFormsPlot.Plot.Layout.LayoutEngine = new BitChartLayoutEngine(formsPlot.Plot);

            foreach (BitCurveDefinition definition in BitCurveDefinitions)
            {
                if (definition.SelectedByDefault)
                    SetBitCurveVisibility(definition, true);
            }

            bitSelector = new TreeView
            {
                Dock = DockStyle.Right,
                Width = 220,
                CheckBoxes = true,
                HideSelection = false,
                ShowLines = true,
                ShowPlusMinus = true,
                Font = new Font("Microsoft YaHei", 9F)
            };
            // 状态曲线在前、故障曲线在后，各为一棵根节点组，勾选状态互不影响。
            foreach (var group in new[]
                     {
                         (Name: "状态曲线", Items: StatusCurveDefinitions),
                         (Name: "故障曲线", Items: FaultCurveDefinitions)
                     })
            {
                var groupNode = new TreeNode(group.Name);
                foreach (var definitionGroup in group.Items.GroupBy(item => new { item.Address, item.GroupName }))
                {
                    var subNode = new TreeNode(definitionGroup.Key.GroupName);
                    foreach (BitCurveDefinition definition in definitionGroup)
                    {
                        subNode.Nodes.Add(new TreeNode(definition.Name)
                        {
                            Tag = definition,
                            Checked = definition.SelectedByDefault
                        });
                    }
                    groupNode.Nodes.Add(subNode);
                }
                bitSelector.Nodes.Add(groupNode);
            }
            foreach (TreeNode node in bitSelector.Nodes)
                node.Expand();

            bool updatingBitChecks = false;
            bitSelector.AfterCheck += (s, e) =>
            {
                if (updatingBitChecks || e.Node is not TreeNode checkedNode)
                    return;

                // 树为三层结构（状态/故障根组 → 寄存器分组 → 曲线叶子），
                // 勾选组节点时需递归传播到所有后代叶子，否则深层曲线不会创建。
                void ApplyToDescendants(TreeNode node, bool checkedState)
                {
                    foreach (TreeNode child in node.Nodes)
                    {
                        child.Checked = checkedState;
                        if (child.Tag is BitCurveDefinition childDefinition)
                            SetBitCurveVisibility(childDefinition, child.Checked);
                        ApplyToDescendants(child, checkedState);
                    }
                }

                updatingBitChecks = true;
                try
                {
                    if (checkedNode.Tag is BitCurveDefinition definition)
                    {
                        SetBitCurveVisibility(definition, checkedNode.Checked);
                    }
                    else
                    {
                        ApplyToDescendants(checkedNode, checkedNode.Checked);
                    }
                }
                finally
                {
                    updatingBitChecks = false;
                }

                if (_followCurrentTime)
                    AutoScaleBitChart();
                else
                    bitFormsPlot.Refresh();
            };

            var bitPanel = new Panel { Dock = DockStyle.Fill };
            bitPanel.Controls.Add(bitFormsPlot);
            bitPanel.Controls.Add(bitSelector);
            chartSplit.Panel2.Controls.Add(bitPanel);
            chartHost.Controls.Add(chartSplit);

            splitRight.Panel1.Controls.Add(panelStatus);
            splitRight.Panel2.Controls.Add(chartHost);

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
                chartSplit.SplitterDistance = Math.Max(120, chartSplit.Height / 2);
            };

            // 预填充所有寄存器到表格
            InitRegisterTable();
        }

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
