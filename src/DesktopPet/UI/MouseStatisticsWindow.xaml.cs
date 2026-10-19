using DesktopPet.Core;
using DesktopPet.Data;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopPet.UI;

public partial class MouseStatisticsWindow : Window
{
    private const double MaxMapWidth = 734;
    private const double HorizontalChrome = 28;
    private const double DefaultChromeHeight = 98;
    private const double MinWindowWidth = 720;
    private const double FixedWindowHeight = 384;
    private const double PlotPadding = 4;
    private const double TrendPlotLeft = 48;
    private const double TrendPlotTop = 18;
    private const double TrendPlotRight = 14;
    private const double TrendPlotBottom = 32;
    private const int MaxTrailPoints = 45;
    private static readonly TimeSpan TrailDuration = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan LiveRepaintInterval = TimeSpan.FromMilliseconds(400);
    private static readonly Color AccentColor = Color.FromRgb(141, 122, 184);
    private static readonly Color[] ButtonColors =
    [
        Color.FromRgb(141, 122, 184),
        Color.FromRgb(201, 142, 154),
        Color.FromRgb(143, 176, 201),
        Color.FromRgb(167, 201, 160)
    ];
    private static readonly string[] ButtonNames = ["左键", "右键", "中键", "滚轮"];
    private static readonly Color[] TrendColors =
    [
        .. ButtonColors,
        Color.FromRgb(218, 168, 92)
    ];
    private static readonly string[] TrendNames = [.. ButtonNames, "移动距离"];

    private readonly DesktopPetRepository _repository;
    private readonly Action _flushPending;
    private readonly Action<bool[]> _saveLegendHidden;
    private readonly DispatcherTimer _liveRepaintTimer;
    private readonly StackPanel[] _legendEntries = new StackPanel[ButtonColors.Length];
    private readonly TextBlock[] _legendLabels = new TextBlock[ButtonColors.Length];
    private readonly StackPanel[] _trendLegendEntries = new StackPanel[TrendColors.Length];
    private readonly bool[] _visibleButtons;
    private readonly Dictionary<(int X, int Y), long> _moves = [];
    private readonly Dictionary<(int X, int Y, int Button), long> _clicks = [];
    private readonly Dictionary<(int X, int Y), long> _liveMoves = [];
    private readonly Dictionary<(int X, int Y, int Button), long> _liveClicks = [];
    private readonly List<(Point Position, DateTime Time)> _trail = [];
    private WriteableBitmap _heatmap = new(1, 1, 96, 96, PixelFormats.Bgra32, null);
    private int[] _heatmapBuffer = new int[1];
    private MouseDailyTotals _totals = MouseDailyTotals.Empty;
    private MouseDailyTotals _liveTotals = MouseDailyTotals.Empty;
    private IReadOnlyDictionary<DateOnly, MouseDailyTotals> _dailySeries = new Dictionary<DateOnly, MouseDailyTotals>();
    private HashSet<DateOnly> _datesWithData = [];
    private DateTime? _rangeStart;
    private DateTime? _rangeEnd;
    private DateTime? _rangeAnchor;
    private bool _updatingDates;
    private bool _liveRange;
    private bool _heatmapDirty = true;
    private bool _showingTrend;
    private bool _trendDirty = true;
    private bool _trendRenderPending;
    private bool _trendRangeValid;
    private int _referenceWidth = 1;
    private int _referenceHeight = 1;
    private double _mapWidth = MaxMapWidth;
    private double _mapHeight = MaxMapWidth * 9 / 16;
    private double _chromeHeight;
    private double _liveDistance;
    private int _lastLiveX;
    private int _lastLiveY;
    private bool _hasLastLive;
    private DateOnly _trendStart;
    private DateOnly _trendEnd;
    private DateOnly _todayAtRefresh;
    private Point? _lastTrendPointer;

    public MouseStatisticsWindow(DesktopPetRepository repository, MouseStatisticsService mouseStatistics, bool[] hiddenButtons, Action<bool[]> saveLegendHidden)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _flushPending = mouseStatistics.Flush;
        _saveLegendHidden = saveLegendHidden;
        _visibleButtons = new bool[TrendColors.Length];
        for (var index = 0; index < _visibleButtons.Length; index++) _visibleButtons[index] = !(index < hiddenButtons.Length && hiddenButtons[index]);
        _liveRepaintTimer = new DispatcherTimer { Interval = LiveRepaintInterval };
        _liveRepaintTimer.Tick += (_, _) => OnLiveRepaintTick();
        _liveRepaintTimer.Start();
        mouseStatistics.MouseMoved += OnMouseMoved;
        mouseStatistics.MouseClicked += OnMouseClicked;
        Closed += (_, _) =>
        {
            mouseStatistics.MouseMoved -= OnMouseMoved;
            mouseStatistics.MouseClicked -= OnMouseClicked;
            _liveRepaintTimer.Stop();
        };
        RangeCalendar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(CalendarPreviewMouseDown), handledEventsToo: true);
        BuildLegend();
        BuildTrendLegend();
        UpdateLegendCounts();
        UpdateViewState();
        Loaded += OnLoaded;
        UpdateMapLayout();
        RangeBox.SelectedIndex = 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var mapRowHeight = StatisticsLayout.RowDefinitions[1].ActualHeight;
        var availableMapHeight = mapRowHeight
            - MapHost.Margin.Top - MapHost.Margin.Bottom
            - MapHost.BorderThickness.Top - MapHost.BorderThickness.Bottom;
        if (availableMapHeight > 0) _chromeHeight = FixedWindowHeight - availableMapHeight;
        SizeToContent = SizeToContent.Manual;
        UpdateMapLayout();
        RenderHeatmap();
        RenderClickMarkers();
    }

    public void RefreshStatistics()
    {
        _flushPending();
        _datesWithData = _repository.GetMouseStatisticDates().ToHashSet();
        UpdateMapLayout();
        var (start, end) = GetSelectedRange();
        _moves.Clear();
        foreach (var (cell, count) in _repository.GetMouseHeatmap(start, end)) _moves[cell] = count;
        _clicks.Clear();
        foreach (var (key, count) in _repository.GetMouseClicks(start, end)) _clicks[key] = count;
        _totals = _repository.GetMouseDailyTotals(start, end);
        _dailySeries = _repository.GetMouseDailySeries(start, end);
        _liveMoves.Clear();
        _liveClicks.Clear();
        _liveTotals = MouseDailyTotals.Empty;
        _liveDistance = 0;
        _hasLastLive = false;
        _trail.Clear();
        _liveRange = RangeIncludesToday(start, end);
        _todayAtRefresh = DateOnly.FromDateTime(DateTime.Now);
        SetTrendRange(start, end);
        _heatmapDirty = true;
        _trendDirty = true;
        RenderHeatmap();
        RenderClickMarkers();
        RenderTrend();
        TrailCanvas.Children.Clear();
        UpdateTotalsText();
    }

    private static bool RangeIncludesToday(DateOnly? start, DateOnly? end)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (start is { } from && today < from) return false;
        if (end is { } to && today > to) return false;
        return true;
    }

    private void OnLiveRepaintTick()
    {
        if (!_liveRange || !IsVisible) return;
        if (_showingTrend)
        {
            if (_trendDirty) RenderTrend();
        }
        else if (_heatmapDirty)
        {
            RenderHeatmap();
        }
        UpdateTotalsText();
    }

    private void OnMouseMoved(object? sender, MousePointEventArgs e)
    {
        if (!_liveRange || !IsVisible) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_todayAtRefresh != default && today != _todayAtRefresh)
        {
            RefreshStatistics();
            return;
        }
        if (_hasLastLive)
        {
            var deltaX = (double)e.X - _lastLiveX;
            var deltaY = (double)e.Y - _lastLiveY;
            _liveDistance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }
        _lastLiveX = e.X;
        _lastLiveY = e.Y;
        _hasLastLive = true;

        var cell = ToCell(e.X, e.Y);
        _liveMoves[cell] = _liveMoves.GetValueOrDefault(cell) + 1;
        _heatmapDirty = true;
        _trendDirty = true;
        AppendTrail(e.X, e.Y);
        UpdateTotalsText();
    }

    private void OnMouseClicked(object? sender, MouseClickEventArgs e)
    {
        if (!_liveRange || !IsVisible) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_todayAtRefresh != default && today != _todayAtRefresh)
        {
            RefreshStatistics();
            return;
        }
        var cell = ToCell(e.X, e.Y);
        var key = (cell.X, cell.Y, (int)e.Button);
        _liveClicks[key] = _liveClicks.GetValueOrDefault(key) + 1;
        _liveTotals = e.Button switch
        {
            MouseButtonKind.Left => _liveTotals.Add(0, 1, 0, 0, 0, e.IsDoubleClick ? 1 : 0),
            MouseButtonKind.Right => _liveTotals.Add(0, 0, 1, 0, 0, 0),
            MouseButtonKind.Middle => _liveTotals.Add(0, 0, 0, 1, 0, 0),
            _ => _liveTotals.Add(0, 0, 0, 0, 1, 0)
        };
        _trendDirty = true;
        if (!_showingTrend)
        {
            RenderClickMarkers();
            ShowClickFlash(MapPoint(e.X, e.Y), e.Button);
        }
        UpdateTotalsText();
    }

    private void UpdateMapLayout()
    {
        (_referenceWidth, _referenceHeight) = MouseGridGeometry.GetPrimaryScreenSize();
        var aspect = (double)_referenceWidth / _referenceHeight;
        var availableHeight = Math.Max(120, FixedWindowHeight - (_chromeHeight > 0 ? _chromeHeight : DefaultChromeHeight));
        _mapHeight = availableHeight;
        _mapWidth = _mapHeight * aspect;
        if (_mapWidth > MaxMapWidth)
        {
            _mapWidth = MaxMapWidth;
            _mapHeight = _mapWidth / aspect;
        }
        MapHost.Width = _mapWidth + 2;
        MapHost.Height = _mapHeight + 2;
        Width = Math.Max(MinWindowWidth, _mapWidth + HorizontalChrome);
        Height = FixedWindowHeight;
        var bitmapWidth = Math.Max(1, (int)Math.Round(_mapWidth));
        var bitmapHeight = Math.Max(1, (int)Math.Round(_mapHeight));
        if (_heatmap.PixelWidth != bitmapWidth || _heatmap.PixelHeight != bitmapHeight)
        {
            _heatmap = new WriteableBitmap(bitmapWidth, bitmapHeight, 96, 96, PixelFormats.Bgra32, null);
            _heatmapBuffer = new int[bitmapWidth * bitmapHeight];
            HeatmapImage.Source = _heatmap;
            _heatmapDirty = true;
        }
    }

    private void BuildLegend()
    {
        for (var index = 0; index < ButtonColors.Length; index++)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(ButtonColors[index]), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Margin = new Thickness(5, 0, 0, 0), FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(101, 86, 127)), VerticalAlignment = VerticalAlignment.Center };
            var entry = new StackPanel { Width = 112, Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Tag = index, ToolTip = "点击隐藏 / 显示该颜色的圆点" };
            entry.Children.Add(dot);
            entry.Children.Add(label);
            entry.MouseLeftButtonUp += ToggleLegendButton;
            _legendEntries[index] = entry;
            _legendLabels[index] = label;
            LegendPanel.Children.Add(entry);
            UpdateLegendEntry(index);
        }
    }

    private void BuildTrendLegend()
    {
        for (var index = 0; index < TrendColors.Length; index++)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(TrendColors[index]), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock
            {
                Text = TrendNames[index],
                Margin = new Thickness(5, 0, 0, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(101, 86, 127)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var entry = new StackPanel
            {
                Width = index == TrendColors.Length - 1 ? 116 : 92,
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = index,
                ToolTip = "点击隐藏 / 显示该趋势"
            };
            entry.Children.Add(dot);
            entry.Children.Add(label);
            entry.MouseLeftButtonUp += ToggleLegendButton;
            _trendLegendEntries[index] = entry;
            TrendLegendPanel.Children.Add(entry);
            UpdateLegendEntry(index);
        }
    }

    private void ToggleLegendButton(object sender, MouseButtonEventArgs e)
    {
        var index = (int)((FrameworkElement)sender).Tag;
        _visibleButtons[index] = !_visibleButtons[index];
        UpdateLegendEntry(index);
        if (_showingTrend)
        {
            _trendDirty = true;
            RequestTrendRender();
        }
        else
        {
            RenderClickMarkers();
        }
        var hidden = new bool[_visibleButtons.Length];
        for (var button = 0; button < hidden.Length; button++) hidden[button] = !_visibleButtons[button];
        _saveLegendHidden(hidden);
    }

    private void UpdateLegendEntry(int index)
    {
        if (index < _legendEntries.Length && _legendEntries[index] is { } entry)
        {
            entry.Opacity = _visibleButtons[index] ? 1 : 0.35;
            if (entry.Children[1] is TextBlock label) label.TextDecorations = _visibleButtons[index] ? null : TextDecorations.Strikethrough;
        }
        if (index < _trendLegendEntries.Length && _trendLegendEntries[index] is { } trendEntry)
        {
            trendEntry.Opacity = _visibleButtons[index] ? 1 : 0.35;
            if (trendEntry.Children[1] is TextBlock label) label.TextDecorations = _visibleButtons[index] ? null : TextDecorations.Strikethrough;
        }
    }

    private void ShowDistributionView(object sender, RoutedEventArgs e)
    {
        _showingTrend = false;
        _lastTrendPointer = null;
        TrendHoverCanvas.Children.Clear();
        UpdateViewState();
        RenderHeatmap();
        RenderClickMarkers();
    }

    private void ShowTrendView(object sender, RoutedEventArgs e)
    {
        _showingTrend = true;
        UpdateViewState();
        RequestTrendRender();
    }

    private void UpdateViewState()
    {
        MapHost.Visibility = _showingTrend ? Visibility.Collapsed : Visibility.Visible;
        LegendPanel.Visibility = _showingTrend ? Visibility.Collapsed : Visibility.Visible;
        TrendHost.Visibility = _showingTrend ? Visibility.Visible : Visibility.Collapsed;
        TrendLegendPanel.Visibility = _showingTrend ? Visibility.Visible : Visibility.Collapsed;
        SetViewButtonAppearance(DistributionViewButton, !_showingTrend);
        SetViewButtonAppearance(TrendViewButton, _showingTrend);
    }

    private static void SetViewButtonAppearance(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? AccentColor : Color.FromRgb(244, 239, 248));
        button.Foreground = new SolidColorBrush(selected ? Colors.White : Color.FromRgb(101, 86, 127));
        button.BorderBrush = new SolidColorBrush(selected ? AccentColor : Color.FromRgb(205, 191, 224));
    }

    private void SetTrendRange(DateOnly? start, DateOnly? end)
    {
        if (start is { } from && end is { } to)
        {
            _trendRangeValid = from <= to;
            _trendStart = _trendRangeValid ? from : _todayAtRefresh;
            _trendEnd = _trendRangeValid ? to : _todayAtRefresh;
            return;
        }

        _trendRangeValid = true;
        if (_dailySeries.Count > 0)
        {
            _trendStart = _dailySeries.Keys.Min();
            _trendEnd = _dailySeries.Keys.Max();
        }
        else
        {
            _trendStart = _todayAtRefresh;
            _trendEnd = _todayAtRefresh;
        }
    }

    private MouseDailyTotals DailyTotals(DateOnly date)
    {
        var totals = _dailySeries.GetValueOrDefault(date) ?? MouseDailyTotals.Empty;
        if (date != _todayAtRefresh) return totals;
        return totals.Add(
            _liveDistance,
            _liveTotals.LeftClicks,
            _liveTotals.RightClicks,
            _liveTotals.MiddleClicks,
            _liveTotals.WheelUnits,
            _liveTotals.DoubleClicks);
    }

    private static double TrendValue(MouseDailyTotals totals, int index) => index switch
    {
        0 => totals.LeftClicks,
        1 => totals.RightClicks,
        2 => totals.MiddleClicks,
        3 => totals.WheelUnits,
        _ => totals.MoveDistance
    };

    private bool HasAnyTrendData()
    {
        foreach (var totals in _dailySeries.Values)
            for (var index = 0; index < TrendColors.Length; index++)
                if (TrendValue(totals, index) > 0) return true;
        var live = DailyTotals(_todayAtRefresh);
        for (var index = 0; index < TrendColors.Length; index++)
            if (TrendValue(live, index) > 0) return true;
        return false;
    }

    private void TrendHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_showingTrend) RequestTrendRender();
    }

    private void RequestTrendRender()
    {
        if (_trendRenderPending) return;
        _trendRenderPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _trendRenderPending = false;
            RenderTrend();
        }, DispatcherPriority.Background);
    }

    private void RenderTrend()
    {
        if (!_showingTrend) return;
        TrendCanvas.Children.Clear();
        TrendHoverCanvas.Children.Clear();
        var width = TrendCanvas.ActualWidth;
        var height = TrendCanvas.ActualHeight;
        if (width < TrendPlotLeft + TrendPlotRight + 20 || height < TrendPlotTop + TrendPlotBottom + 20) return;
        _trendDirty = false;

        if (!_trendRangeValid || !HasAnyTrendData())
        {
            AddTrendEmptyMessage(width, height, "该范围暂无鼠标记录");
            return;
        }
        if (!_visibleButtons.Any(visible => visible))
        {
            AddTrendEmptyMessage(width, height, "请在下方选择至少一项趋势");
            return;
        }

        var plotWidth = width - TrendPlotLeft - TrendPlotRight;
        var plotHeight = height - TrendPlotTop - TrendPlotBottom;
        var maxima = GetTrendMaxima();
        if (!Enumerable.Range(0, TrendColors.Length).Any(index => _visibleButtons[index] && maxima[index] > 0))
        {
            AddTrendEmptyMessage(width, height, "所选项目在该范围暂无记录");
            return;
        }
        const int yDivisions = 4;
        for (var index = 0; index <= yDivisions; index++)
        {
            var y = TrendPlotTop + plotHeight - index * plotHeight / yDivisions;
            TrendCanvas.Children.Add(new Line { X1 = TrendPlotLeft, X2 = width - TrendPlotRight, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromRgb(225, 217, 235)), StrokeThickness = 1 });
            var label = new TextBlock
            {
                Text = $"{index * 25}%",
                Foreground = new SolidColorBrush(Color.FromRgb(137, 127, 150)),
                FontSize = 10,
                Width = TrendPlotLeft - 7,
                TextAlignment = TextAlignment.Right
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            TrendCanvas.Children.Add(label);
        }

        foreach (var tick in GetTrendHorizontalTicks(plotWidth))
        {
            var x = TrendDateX(tick, plotWidth);
            TrendCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = TrendPlotTop, Y2 = TrendPlotTop + plotHeight, Stroke = new SolidColorBrush(Color.FromRgb(225, 217, 235)), StrokeThickness = 1, Opacity = .65 });
            var label = new TextBlock { Text = FormatTrendHorizontalTick(tick), Foreground = new SolidColorBrush(Color.FromRgb(137, 127, 150)), FontSize = 10, Width = 68, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, Math.Clamp(x - 34, 0, Math.Max(0, width - 68)));
            Canvas.SetTop(label, TrendPlotTop + plotHeight + 7);
            TrendCanvas.Children.Add(label);
        }

        for (var index = 0; index < TrendColors.Length; index++)
        {
            if (!_visibleButtons[index] || maxima[index] <= 0) continue;
            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(TrendColors[index]),
                StrokeThickness = 2.1,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var (date, value) in BuildTrendSeries(index))
            {
                var normalized = value / maxima[index];
                polyline.Points.Add(new Point(TrendDateX(date, plotWidth), TrendValueY(normalized, plotHeight)));
            }
            TrendCanvas.Children.Add(polyline);

            if (_trendStart == _trendEnd)
            {
                var normalized = TrendValue(DailyTotals(_trendStart), index) / maxima[index];
                var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(TrendColors[index]), Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(dot, TrendDateX(_trendStart, plotWidth) - 3.5);
                Canvas.SetTop(dot, TrendValueY(normalized, plotHeight) - 3.5);
                TrendCanvas.Children.Add(dot);
            }
        }

        if (_lastTrendPointer is { } pointer) RenderTrendHover(pointer);
    }

    private void AddTrendEmptyMessage(double width, double height, string message)
    {
        var empty = new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Color.FromRgb(137, 127, 150)),
            FontSize = 13,
            Width = width,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetTop(empty, Math.Max(0, height / 2 - 10));
        TrendCanvas.Children.Add(empty);
    }

    private double[] GetTrendMaxima()
    {
        var maxima = new double[TrendColors.Length];
        foreach (var date in _dailySeries.Keys)
        {
            if (date < _trendStart || date > _trendEnd) continue;
            var totals = DailyTotals(date);
            for (var index = 0; index < maxima.Length; index++) maxima[index] = Math.Max(maxima[index], TrendValue(totals, index));
        }
        if (_todayAtRefresh >= _trendStart && _todayAtRefresh <= _trendEnd)
        {
            var totals = DailyTotals(_todayAtRefresh);
            for (var index = 0; index < maxima.Length; index++) maxima[index] = Math.Max(maxima[index], TrendValue(totals, index));
        }
        return maxima;
    }

    private IReadOnlyList<(DateOnly Date, double Value)> BuildTrendSeries(int index)
    {
        var values = new SortedDictionary<DateOnly, double>();
        foreach (var (date, _) in _dailySeries)
        {
            if (date < _trendStart || date > _trendEnd) continue;
            var value = TrendValue(DailyTotals(date), index);
            if (value > 0) values[date] = value;
        }
        if (_todayAtRefresh >= _trendStart && _todayAtRefresh <= _trendEnd)
        {
            var value = TrendValue(DailyTotals(_todayAtRefresh), index);
            if (value > 0) values[_todayAtRefresh] = value;
        }

        var series = new List<(DateOnly Date, double Value)>();
        AddTrendSeriesPoint(series, _trendStart, values.GetValueOrDefault(_trendStart));
        var previous = _trendStart;
        foreach (var (date, value) in values)
        {
            var gap = date.DayNumber - previous.DayNumber;
            if (gap > 1) AddTrendSeriesPoint(series, previous.AddDays(1), 0);
            if (gap > 2) AddTrendSeriesPoint(series, date.AddDays(-1), 0);
            AddTrendSeriesPoint(series, date, value);
            previous = date;
        }
        var endGap = _trendEnd.DayNumber - previous.DayNumber;
        if (endGap > 1) AddTrendSeriesPoint(series, previous.AddDays(1), 0);
        AddTrendSeriesPoint(series, _trendEnd, values.GetValueOrDefault(_trendEnd));
        return series;
    }

    private static void AddTrendSeriesPoint(List<(DateOnly Date, double Value)> series, DateOnly date, double value)
    {
        if (series.Count > 0 && series[^1].Date == date) series[^1] = (date, value);
        else series.Add((date, value));
    }

    private IEnumerable<DateOnly> GetTrendHorizontalTicks(double plotWidth)
    {
        var spanDays = _trendEnd.DayNumber - _trendStart.DayNumber + 1;
        if (spanDays <= 31)
        {
            var step = ChooseTrendInterval(Math.Ceiling(spanDays * 52 / Math.Max(1, plotWidth)), [1, 2, 3, 5, 7, 10, 14]);
            for (var date = _trendStart; date <= _trendEnd;)
            {
                yield return date;
                if (_trendEnd.DayNumber - date.DayNumber < step) break;
                date = date.AddDays(step);
            }
            yield break;
        }

        if (spanDays <= 180)
        {
            var offset = ((int)DayOfWeek.Monday - (int)_trendStart.DayOfWeek + 7) % 7;
            var first = _trendStart.AddDays(offset);
            var weeks = Math.Max(1, (spanDays + 6) / 7);
            var step = ChooseTrendInterval(Math.Ceiling(weeks * 58 / Math.Max(1, plotWidth)), [1, 2, 4, 8]);
            for (var date = first; date <= _trendEnd;)
            {
                yield return date;
                var stepDays = step * 7;
                if (_trendEnd.DayNumber - date.DayNumber < stepDays) break;
                date = date.AddDays(stepDays);
            }
            yield break;
        }

        var firstMonth = new DateOnly(_trendStart.Year, _trendStart.Month, 1);
        if (firstMonth < _trendStart)
        {
            if (_trendStart.Year == DateOnly.MaxValue.Year && _trendStart.Month == DateOnly.MaxValue.Month) yield break;
            firstMonth = firstMonth.AddMonths(1);
        }
        var months = Math.Max(1, (_trendEnd.Year - _trendStart.Year) * 12 + _trendEnd.Month - _trendStart.Month + 1);
        var monthStep = ChooseTrendInterval(Math.Ceiling(months * 66 / Math.Max(1, plotWidth)), [1, 2, 3, 6, 12, 24, 36, 60]);
        for (var date = firstMonth; date <= _trendEnd;)
        {
            yield return date;
            var remainingMonths = (_trendEnd.Year - date.Year) * 12 + _trendEnd.Month - date.Month;
            if (remainingMonths < monthStep) break;
            date = date.AddMonths(monthStep);
        }
    }

    private string FormatTrendHorizontalTick(DateOnly date)
    {
        var spanDays = _trendEnd.DayNumber - _trendStart.DayNumber + 1;
        if (spanDays <= 180) return date.ToString("MM-dd");
        return _trendStart.Year == _trendEnd.Year ? date.ToString("MM月") : date.ToString("yyyy-MM");
    }

    private static int ChooseTrendInterval(double minimum, IReadOnlyList<int> candidates)
    {
        foreach (var candidate in candidates) if (candidate >= minimum) return candidate;
        var largest = candidates[^1];
        return Math.Max(largest, (int)Math.Ceiling(minimum / largest) * largest);
    }

    private double TrendDateX(DateOnly date, double plotWidth)
    {
        var span = _trendEnd.DayNumber - _trendStart.DayNumber;
        return span == 0 ? TrendPlotLeft + plotWidth / 2 : TrendPlotLeft + (date.DayNumber - _trendStart.DayNumber) * plotWidth / span;
    }

    private static double TrendValueY(double normalized, double plotHeight) => TrendPlotTop + plotHeight * (1 - Math.Clamp(normalized, 0, 1));

    private void TrendMouseMove(object sender, MouseEventArgs e)
    {
        var pointer = e.GetPosition(TrendHoverCanvas);
        _lastTrendPointer = pointer;
        RenderTrendHover(pointer);
    }

    private void RenderTrendHover(Point pointer)
    {
        TrendHoverCanvas.Children.Clear();
        if (!_showingTrend || !_trendRangeValid || !HasAnyTrendData() || !_visibleButtons.Any(visible => visible)) return;
        var width = TrendHoverCanvas.ActualWidth;
        var height = TrendHoverCanvas.ActualHeight;
        var plotWidth = width - TrendPlotLeft - TrendPlotRight;
        var plotHeight = height - TrendPlotTop - TrendPlotBottom;
        if (plotWidth <= 0 || plotHeight <= 0) return;
        if (pointer.X < TrendPlotLeft || pointer.X > TrendPlotLeft + plotWidth || pointer.Y < TrendPlotTop || pointer.Y > TrendPlotTop + plotHeight) return;

        var span = _trendEnd.DayNumber - _trendStart.DayNumber;
        var dayOffset = span == 0 ? 0 : (int)Math.Round((pointer.X - TrendPlotLeft) / plotWidth * span);
        var date = _trendStart.AddDays(Math.Clamp(dayOffset, 0, span));
        var totals = DailyTotals(date);
        var maxima = GetTrendMaxima();
        var x = TrendDateX(date, plotWidth);
        TrendHoverCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = TrendPlotTop, Y2 = TrendPlotTop + plotHeight, Stroke = new SolidColorBrush(AccentColor), StrokeThickness = 1, Opacity = .65 });

        var tooltipContent = new StackPanel();
        tooltipContent.Children.Add(new TextBlock { Text = date.ToString("yyyy-MM-dd"), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(101, 86, 127)) });
        for (var index = 0; index < TrendColors.Length; index++)
        {
            if (!_visibleButtons[index]) continue;
            var value = TrendValue(totals, index);
            if (maxima[index] > 0)
            {
                var y = TrendValueY(value / maxima[index], plotHeight);
                var dot = new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(TrendColors[index]), Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(dot, x - 3.5);
                Canvas.SetTop(dot, y - 3.5);
                TrendHoverCanvas.Children.Add(dot);
            }
            tooltipContent.Children.Add(new TextBlock
            {
                Text = $"● {TrendNames[index]}  {FormatTrendValue(totals, index)}",
                Foreground = new SolidColorBrush(TrendColors[index]),
                FontSize = 11
            });
        }

        var tooltip = new Border { Padding = new Thickness(7, 5, 7, 5), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(252, 250, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(205, 191, 224)), BorderThickness = new Thickness(1), Child = tooltipContent };
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(x + 8, 2, Math.Max(2, width - tooltip.DesiredSize.Width - 2));
        var top = Math.Clamp(pointer.Y - tooltip.DesiredSize.Height / 2, 2, Math.Max(2, height - tooltip.DesiredSize.Height - 2));
        Canvas.SetLeft(tooltip, left);
        Canvas.SetTop(tooltip, top);
        TrendHoverCanvas.Children.Add(tooltip);
    }

    private static string FormatTrendValue(MouseDailyTotals totals, int index) => index switch
    {
        0 => $"{totals.LeftClicks:N0} 次",
        1 => $"{totals.RightClicks:N0} 次",
        2 => $"{totals.MiddleClicks:N0} 次",
        3 => $"{totals.WheelUnits:N0} 格",
        _ => FormatDistance(totals.MoveDistance)
    };

    private void TrendMouseLeave(object sender, MouseEventArgs e)
    {
        _lastTrendPointer = null;
        TrendHoverCanvas.Children.Clear();
    }

    private void RenderHeatmap()
    {
        var width = _heatmap.PixelWidth;
        var height = _heatmap.PixelHeight;
        if (width <= 1 || height <= 1) return;
        Array.Clear(_heatmapBuffer, 0, _heatmapBuffer.Length);

        var merged = new Dictionary<(int X, int Y), long>(_moves);
        foreach (var (cell, count) in _liveMoves) merged[cell] = merged.GetValueOrDefault(cell) + count;
        long maximum = 1;
        foreach (var count in merged.Values) if (count > maximum) maximum = count;

        foreach (var (cell, count) in merged)
        {
            var x0 = PlotBoundary(cell.X, width, MouseGridGeometry.Columns);
            var y0 = PlotBoundary(cell.Y, height, MouseGridGeometry.Rows);
            var x1 = PlotBoundary(cell.X + 1, width, MouseGridGeometry.Columns);
            var y1 = PlotBoundary(cell.Y + 1, height, MouseGridGeometry.Rows);
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > width) x1 = width;
            if (y1 > height) y1 = height;
            if (x1 <= x0 || y1 <= y0) continue;
            var pixel = HeatPixel(count, maximum);
            for (var y = y0; y < y1; y++)
            {
                var row = y * width;
                for (var x = x0; x < x1; x++) _heatmapBuffer[row + x] = pixel;
            }
        }

        _heatmap.WritePixels(new Int32Rect(0, 0, width, height), _heatmapBuffer, width * 4, 0);
        _heatmapDirty = false;
    }

    private static int HeatPixel(long count, long maximum)
    {
        var ratio = Math.Sqrt((double)count / Math.Max(1, maximum));
        var red = (byte)(246 - 105 * ratio);
        var green = (byte)(242 - 120 * ratio);
        var blue = (byte)(251 - 67 * ratio);
        var alpha = (byte)(45 + 170 * ratio);
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    private void RenderClickMarkers()
    {
        MarkerCanvas.Children.Clear();
        var merged = new Dictionary<(int X, int Y), long[]>();
        foreach (var ((x, y, button), count) in _clicks) { if (_visibleButtons[button]) AccumulateCell(merged, x, y, button, count); }
        foreach (var ((x, y, button), count) in _liveClicks) { if (_visibleButtons[button]) AccumulateCell(merged, x, y, button, count); }
        if (merged.Count == 0) return;
        var maximum = Math.Max(1, merged.Values.Max(counts => counts.Sum()));

        foreach (var ((x, y), counts) in merged)
        {
            var total = counts.Sum();
            var position = CellCenter((x, y));
            var diameter = 4 + 12 * Math.Sqrt((double)total / maximum);
            var color = ButtonColors[DominantButton(counts)];
            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1,
                ToolTip = BuildClickTooltip(counts)
            };
            Canvas.SetLeft(ellipse, position.X - diameter / 2);
            Canvas.SetTop(ellipse, position.Y - diameter / 2);
            MarkerCanvas.Children.Add(ellipse);
        }
    }

    private static void AccumulateCell(Dictionary<(int X, int Y), long[]> cells, int x, int y, int button, long count)
    {
        if (!cells.TryGetValue((x, y), out var counts)) cells[(x, y)] = counts = new long[ButtonNames.Length];
        counts[button] += count;
    }

    private static int DominantButton(long[] counts)
    {
        var dominant = 0;
        for (var index = 1; index < counts.Length; index++)
            if (counts[index] > counts[dominant]) dominant = index;
        return dominant;
    }

    private static string BuildClickTooltip(long[] counts)
    {
        var lines = new List<string>();
        for (var index = 0; index < counts.Length; index++)
        {
            if (counts[index] <= 0) continue;
            var unit = index == (int)MouseButtonKind.Wheel ? "格" : "次";
            lines.Add($"{ButtonNames[index]} {counts[index]:N0} {unit}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void ShowClickFlash(Point position, MouseButtonKind button)
    {
        const double diameter = 16;
        var color = ButtonColors[(int)button];
        var dot = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = new SolidColorBrush(Color.FromArgb(120, color.R, color.G, color.B)),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
            RenderTransform = new ScaleTransform(1, 1),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        Canvas.SetLeft(dot, position.X - diameter / 2);
        Canvas.SetTop(dot, position.Y - diameter / 2);
        MarkerCanvas.Children.Add(dot);

        var fade = new DoubleAnimation
        {
            From = 0.9,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(120),
            Duration = TimeSpan.FromMilliseconds(260),
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => MarkerCanvas.Children.Remove(dot);
        dot.BeginAnimation(OpacityProperty, fade);
        if (dot.RenderTransform is ScaleTransform scale)
        {
            var grow = new DoubleAnimation { From = 0.4, To = 1.6, Duration = TimeSpan.FromMilliseconds(220), FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
    }

    private void AppendTrail(int x, int y)
    {
        var now = DateTime.UtcNow;
        _trail.Add((MapPoint(x, y), now));
        while (_trail.Count > 0 && now - _trail[0].Time > TrailDuration) _trail.RemoveAt(0);
        while (_trail.Count > MaxTrailPoints) _trail.RemoveAt(0);
        RebuildTrail();
    }

    private void RebuildTrail()
    {
        TrailCanvas.Children.Clear();
        if (_trail.Count < 2) return;
        var now = DateTime.UtcNow;
        var accent = new SolidColorBrush(AccentColor);
        for (var index = 1; index < _trail.Count; index++)
        {
            var age = (now - _trail[index].Time).TotalSeconds;
            var factor = Math.Clamp(1 - age / TrailDuration.TotalSeconds, 0, 1);
            TrailCanvas.Children.Add(new Line
            {
                X1 = _trail[index - 1].Position.X,
                Y1 = _trail[index - 1].Position.Y,
                X2 = _trail[index].Position.X,
                Y2 = _trail[index].Position.Y,
                Stroke = accent,
                StrokeThickness = 2,
                Opacity = 0.06 + 0.6 * factor,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }
        var head = _trail[^1].Position;
        var dot = new Ellipse { Width = 6, Height = 6, Fill = accent };
        Canvas.SetLeft(dot, head.X - 3);
        Canvas.SetTop(dot, head.Y - 3);
        TrailCanvas.Children.Add(dot);
    }

    private void UpdateTotalsText()
    {
        var distance = _totals.MoveDistance + _liveDistance;
        MoveText.Text = $"移动 {FormatDistance(distance)}";
        var doubles = _totals.DoubleClicks + _liveTotals.DoubleClicks;
        DoubleClickText.Text = $"双击 {doubles:N0} 次";
        UpdateLegendCounts();
    }

    private void UpdateLegendCounts()
    {
        var counts = new[]
        {
            _totals.LeftClicks + _liveTotals.LeftClicks,
            _totals.RightClicks + _liveTotals.RightClicks,
            _totals.MiddleClicks + _liveTotals.MiddleClicks,
            _totals.WheelUnits + _liveTotals.WheelUnits
        };

        for (var index = 0; index < _legendLabels.Length; index++)
        {
            var unit = index == (int)MouseButtonKind.Wheel ? "格" : "次";
            _legendLabels[index].Text = $"{ButtonNames[index]}（{CompactCountFormatter.Format(counts[index])}{unit}）";
            _legendEntries[index].ToolTip = $"{ButtonNames[index]}：{counts[index]:N0} {unit}\n点击隐藏 / 显示该颜色的圆点";
            if (_trendLegendEntries[index] is { } trendEntry)
                trendEntry.ToolTip = $"{ButtonNames[index]}：{counts[index]:N0} {unit}\n点击隐藏 / 显示该趋势";
        }
        var distance = _totals.MoveDistance + _liveDistance;
        if (_trendLegendEntries[^1] is { } distanceEntry)
            distanceEntry.ToolTip = $"移动距离：{FormatDistance(distance)}\n点击隐藏 / 显示该趋势";
    }

    private static string FormatDistance(double pixels)
    {
        var meters = pixels / 96.0 * 0.0254;
        return meters >= 1000 ? $"{meters / 1000:0.00} km" : $"{meters:0.0} m";
    }

    private static int PlotBoundary(int index, int pixels, int cells)
    {
        var plotSize = Math.Max(1, pixels - 2 * PlotPadding);
        var boundary = (int)Math.Round(PlotPadding + index * plotSize / cells);
        return Math.Clamp(boundary, 0, pixels);
    }

    private static (int X, int Y) ToCell(int x, int y) => MouseGridGeometry.ToCell(x, y);

    private Point CellCenter((int X, int Y) cell) => new(
        PlotPadding + (cell.X + 0.5) * Math.Max(1, _mapWidth - 2 * PlotPadding) / MouseGridGeometry.Columns,
        PlotPadding + (cell.Y + 0.5) * Math.Max(1, _mapHeight - 2 * PlotPadding) / MouseGridGeometry.Rows);

    private Point MapPoint(int x, int y)
    {
        var (left, top, width, height) = MouseGridGeometry.GetMonitorBounds(x, y);
        var normalizedX = Math.Clamp((x - (double)left) / Math.Max(1, width), 0, 1);
        var normalizedY = Math.Clamp((y - (double)top) / Math.Max(1, height), 0, 1);
        var plotWidth = Math.Max(1, _mapWidth - 2 * PlotPadding);
        var plotHeight = Math.Max(1, _mapHeight - 2 * PlotPadding);
        return new Point(
            PlotPadding + normalizedX * plotWidth,
            PlotPadding + normalizedY * plotHeight);
    }

    private void RangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingDates) return;
        if (RangeBox.SelectedIndex != 4) SetPresetDates();
        RefreshStatistics();
    }

    private void SetPresetDates()
    {
        _flushPending();
        _datesWithData = _repository.GetMouseStatisticDates().ToHashSet();
        var today = DateTime.Today;
        var (start, end) = RangeBox.SelectedIndex switch
        {
            1 => (today, today),
            2 => (today.AddDays(-6), today),
            3 => (today.AddDays(-29), today),
            _ when _datesWithData.Count > 0 => (_datesWithData.Min().ToDateTime(TimeOnly.MinValue), _datesWithData.Max().ToDateTime(TimeOnly.MinValue)),
            _ => (today, today)
        };
        SetRange(start, end);
    }

    private void SetRange(DateTime start, DateTime end)
    {
        _rangeStart = start;
        _rangeEnd = end;
        _rangeAnchor = null;
        _updatingDates = true;
        RangeCalendar.SelectedDates.Clear();
        RangeCalendar.SelectedDates.AddRange(start, end);
        RangeCalendar.DisplayDate = end;
        _updatingDates = false;
        UpdateRangeButtonText();
        RefreshDayHighlights();
    }

    private void UpdateRangeButtonText()
    {
        if (RangeBox.SelectedIndex == 0) { RangeButton.Content = "全部时间"; return; }
        if (_rangeStart is { } start && _rangeEnd is { } end)
            RangeButton.Content = start.Date == end.Date ? start.ToString("yyyy-MM-dd") : $"{start:yyyy-MM-dd} ~ {end:yyyy-MM-dd}";
    }

    private void ToggleRangePopup(object sender, RoutedEventArgs e)
    {
        _rangeAnchor = null;
        RangePopup.IsOpen = !RangePopup.IsOpen;
        if (RangePopup.IsOpen) RefreshDayHighlights();
    }

    private void CalendarPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var button = FindAncestor<CalendarDayButton>(e.OriginalSource as DependencyObject);
        if (button?.DataContext is not DateTime date) return;
        if (date.Year != RangeCalendar.DisplayDate.Year || date.Month != RangeCalendar.DisplayDate.Month)
        {
            _rangeAnchor = null;
            return;
        }
        e.Handled = true;
        SelectRangeDay(date.Date);
    }

    private void SelectRangeDay(DateTime date)
    {
        if (_rangeAnchor is null)
        {
            _rangeAnchor = date;
            RangeCalendar.SelectedDates.Clear();
            RangeCalendar.SelectedDates.Add(date);
            return;
        }

        var start = _rangeAnchor.Value <= date ? _rangeAnchor.Value : date;
        var end = _rangeAnchor.Value <= date ? date : _rangeAnchor.Value;
        _rangeAnchor = null;
        RangeCalendar.SelectedDates.Clear();
        RangeCalendar.SelectedDates.AddRange(start, end);
        _rangeStart = start;
        _rangeEnd = end;
        UpdateRangeButtonText();
        RangePopup.IsOpen = false;
        if (RangeBox.SelectedIndex != 4) RangeBox.SelectedIndex = 4;
        else RefreshStatistics();
    }

    private void CalendarDisplayDateChanged(object sender, CalendarDateChangedEventArgs e) => Dispatcher.InvokeAsync(RefreshDayHighlights);

    private void CalendarSelectedDatesChanged(object sender, SelectionChangedEventArgs e) => RefreshDayHighlights();

    private void CalendarDayLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CalendarDayButton button) ApplyDayHighlight(button);
    }

    private void RefreshDayHighlights()
    {
        if (!IsInitialized) return;
        foreach (var button in FindDayButtons(RangeCalendar)) ApplyDayHighlight(button);
    }

    private void ApplyDayHighlight(CalendarDayButton button)
    {
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.FontWeightProperty);
        if (button.DataContext is not DateTime dateTime) return;
        if (RangeCalendar.SelectedDates.Any(selected => selected.Date == dateTime.Date)) return;
        if (!_datesWithData.Contains(DateOnly.FromDateTime(dateTime))) return;
        button.Background = new SolidColorBrush(Color.FromRgb(221, 210, 237));
        button.BorderBrush = new SolidColorBrush(AccentColor);
        button.FontWeight = FontWeights.Bold;
    }

    private static IEnumerable<CalendarDayButton> FindDayButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is CalendarDayButton button) yield return button;
            else foreach (var nested in FindDayButtons(child)) yield return nested;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
            if (current is T typed) return typed;
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private (DateOnly? Start, DateOnly? End) GetSelectedRange()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        return RangeBox.SelectedIndex switch
        {
            1 => (today, today),
            2 => (today.AddDays(-6), today),
            3 => (today.AddDays(-29), today),
            4 when _rangeStart is { } start && _rangeEnd is { } end =>
                (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end)),
            4 => (DateOnly.MaxValue, DateOnly.MinValue),
            _ => (null, null)
        };
    }
}
