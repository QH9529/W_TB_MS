using ScottPlot;

namespace W_TB_MS
{
    /// <summary>
    /// BIT 曲线图布局引擎：左右边界取参数图数据区（保证同一时间垂直对齐），
    /// 上下边界用自身默认布局（保证底轴线固定贴本窗口底部，不随参数图数据区高度浮动）。
    /// </summary>
    internal sealed class BitChartLayoutEngine : ILayoutEngine
    {
        private readonly Plot _referencePlot;
        private readonly ScottPlot.LayoutEngines.Automatic _ownLayout = new();

        public BitChartLayoutEngine(Plot referencePlot)
        {
            _referencePlot = referencePlot;
        }

        public Layout GetLayout(PixelRect figureRect, Plot plot)
        {
            Layout own = _ownLayout.GetLayout(figureRect, plot);
            PixelRect referenceData = _referencePlot.RenderManager.LastRender.DataRect;
            // 参数图尚未渲染（首帧）时先用自身布局，参数图渲染一帧后自动对齐。
            if (!referenceData.HasArea)
                return own;

            var dataRect = new PixelRect(
                referenceData.Left,
                referenceData.Right,
                own.DataRect.Bottom,
                own.DataRect.Top);
            return new Layout(figureRect, dataRect, own.PanelSizes, own.PanelOffsets);
        }
    }
}
