using System.Windows.Forms;

namespace W_TB_MS
{
    /// <summary>
    /// 曲线图悬停 tooltip 共享辅助（MainForm 实时曲线与 HistoryChartForm 历史曲线共用）：
    /// - 去重：文本未变化时不重复 Show，避免 MouseMove 高频调用闪烁；
    /// - 边界：用 Graphics 实测文本尺寸，靠近右/下边缘时翻转到鼠标另一侧并完全让开光标，
    ///   避免 tooltip 覆盖鼠标导致 MouseLeave→Hide→MouseMove→Show 闪烁循环；
    /// - 行数：超过 MaxLines 时截断并提示，防止勾选曲线过多时 tooltip 超出屏幕。
    /// </summary>
    internal sealed class ChartToolTip : IDisposable
    {
        /// <summary>tooltip 最大行数（含时间行），与"悬停显示不超8条曲线"的规格一致。</summary>
        public const int MaxLines = 9;

        private readonly ToolTip _toolTip = new()
        {
            InitialDelay = 0,
            ReshowDelay = 0,
            AutoPopDelay = 30000,
            ShowAlways = true,
            UseAnimation = false,
            UseFading = false
        };
        private string _lastText = string.Empty;
        private Control? _lastControl;

        /// <summary>行数超限时截断文本并追加省略提示。</summary>
        public static string TrimToMaxLines(string text)
        {
            string[] lines = text.Split('\n');
            if (lines.Length <= MaxLines)
                return text;
            return string.Join('\n', lines.Take(MaxLines)) + $"\n…（其余 {lines.Length - MaxLines} 条未显示）";
        }

        public void Show(Control chartControl, MouseEventArgs e, string text)
        {
            text = TrimToMaxLines(text);
            if (_lastText == text && _lastControl == chartControl)
                return;
            _lastText = text;
            _lastControl = chartControl;

            Size size = MeasureText(chartControl, text);
            // 光标热区（含箭头与阴影）约 24x24px，tooltip 必须与光标完全无重叠。
            const int cursorClearance = 24;
            const int screenMargin = 4;
            int x, y;
            if (e.X + cursorClearance + size.Width + screenMargin <= chartControl.ClientSize.Width)
            {
                // 默认：光标右下方
                x = e.X + cursorClearance;
                y = e.Y + cursorClearance;
            }
            else
            {
                // 右侧放不下：翻转到光标左侧，完全让开光标
                x = Math.Max(screenMargin, e.X - cursorClearance - size.Width);
                y = e.Y + cursorClearance;
            }
            if (y + size.Height + screenMargin > chartControl.ClientSize.Height)
            {
                // 下方放不下：向上翻转，让 tooltip 底边贴住光标上方
                y = Math.Max(screenMargin, e.Y - cursorClearance - size.Height);
            }
            _toolTip.Show(text, chartControl, x, y, 30000);
        }

        public void Hide(Control chartControl)
        {
            _toolTip.Hide(chartControl);
            _lastControl = null;
            _lastText = string.Empty;
        }

        /// <summary>用控件实际字体测量 tooltip 文本尺寸，比按字符数估算准确（中文 9pt 实宽约为估算的 1.6 倍）。</summary>
        private static Size MeasureText(Control chartControl, string text)
        {
            using var graphics = chartControl.CreateGraphics();
            Size proposed = new(int.MaxValue, int.MaxValue);
            return Size.Ceiling(graphics.MeasureString(text, chartControl.Font, proposed));
        }

        public void Dispose() => _toolTip.Dispose();
    }
}
