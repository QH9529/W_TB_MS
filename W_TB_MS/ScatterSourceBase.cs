using ScottPlot;

namespace W_TB_MS
{
    /// <summary>
    /// EnumScatterSource/BitScatterSource 公共基类：
    /// 封装共享的时间轴列表、渲染窗口索引、最近点查找与坐标视图，
    /// 子类只需提供取值函数（GetPoint）、值数量与 Y 轴范围。
    /// </summary>
    internal abstract class ScatterSourceBase : IScatterSource
    {
        protected readonly List<double> Times;
        private readonly IReadOnlyList<Coordinates> _points;
        private int _minRenderIndex;
        private int _maxRenderIndex = int.MaxValue;

        protected ScatterSourceBase(List<double> times)
        {
            Times = times;
            _points = new CoordinatesView(this);
        }

        protected abstract int ValueCount { get; }
        protected abstract Coordinates GetPoint(int index);
        public abstract CoordinateRange GetLimitsY();

        public IReadOnlyList<Coordinates> GetScatterPoints() => _points;

        public DataPoint GetNearest(
            Coordinates mouseCoordinates,
            RenderDetails renderDetails,
            float maxDistance)
        {
            int index = FindNearestIndex(mouseCoordinates.X);
            return IsWithinDistance(index, mouseCoordinates, renderDetails, maxDistance)
                ? new DataPoint(GetPoint(index), index)
                : DataPoint.None;
        }

        public DataPoint GetNearestX(
            Coordinates mouseCoordinates,
            RenderDetails renderDetails,
            float maxDistance)
        {
            int index = FindNearestIndex(mouseCoordinates.X);
            if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX))
                return DataPoint.None;

            double distance = Math.Abs((Times[index] - mouseCoordinates.X) * renderDetails.PxPerUnitX);
            return distance <= maxDistance
                ? new DataPoint(GetPoint(index), index)
                : DataPoint.None;
        }

        public CoordinateRange GetLimitsX() =>
            Times.Count == 0
                ? new CoordinateRange(0, 1)
                : new CoordinateRange(Times[0], Times[^1]);

        public AxisLimits GetLimits() =>
            new(GetLimitsX().Min, GetLimitsX().Max, GetLimitsY().Min, GetLimitsY().Max);

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

        internal int FindNearestIndex(double x)
        {
            if (Times.Count == 0 || ValueCount == 0)
                return -1;

            int low = Math.Max(0, _minRenderIndex);
            int high = Math.Min(Math.Min(Times.Count, ValueCount) - 1, _maxRenderIndex);
            if (high < low)
                return -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (Times[middle] < x) low = middle + 1;
                else if (Times[middle] > x) high = middle - 1;
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
            Coordinates mouseCoordinates,
            RenderDetails renderDetails,
            float maxDistance)
        {
            if (index < 0 || !double.IsFinite(renderDetails.PxPerUnitX)
                || !double.IsFinite(renderDetails.PxPerUnitY))
                return false;
            Coordinates point = GetPoint(index);
            double dx = (point.X - mouseCoordinates.X) * renderDetails.PxPerUnitX;
            double dy = (point.Y - mouseCoordinates.Y) * renderDetails.PxPerUnitY;
            return Math.Sqrt(dx * dx + dy * dy) <= maxDistance;
        }

        private sealed class CoordinatesView : IReadOnlyList<Coordinates>
        {
            private readonly ScatterSourceBase _source;
            internal CoordinatesView(ScatterSourceBase source) => _source = source;
            public int Count => Math.Min(_source.Times.Count, _source.ValueCount);
            public Coordinates this[int index] => _source.GetPoint(index);
            public IEnumerator<Coordinates> GetEnumerator()
            {
                for (int i = 0; i < Count; i++) yield return _source.GetPoint(i);
            }
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
