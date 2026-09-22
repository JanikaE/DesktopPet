using DesktopPet.Core;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DesktopPet.UI;

public partial class KeyboardStatisticsWindow : Window
{
    private const double KeyWidth = 35;
    private const double PlotLeft = 58;
    private const double PlotTop = 16;
    private const double PlotRight = 14;
    private const double PlotBottom = 32;
    private static readonly Color LiveFlashColor = Color.FromRgb(141, 122, 184);
    private static readonly Color AccentColor = Color.FromRgb(141, 122, 184);
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(137, 127, 150));
    private static readonly SolidColorBrush GridBrush = new(Color.FromRgb(225, 217, 235));
    private readonly DesktopPetRepository _repository;
    private readonly Action _flushPending;
    private readonly Action<string> _saveLayout;
    private readonly Dictionary<int, List<Border>> _keyViews = [];
    private readonly Dictionary<int, long> _liveIncrements = [];
    private IReadOnlyDictionary<int, long> _counts = new Dictionary<int, long>();
    private IReadOnlyDictionary<DateOnly, long> _dailyTotals = new Dictionary<DateOnly, long>();
    private HashSet<DateOnly> _datesWithData = [];
    private DateTime? _rangeStart;
    private DateTime? _rangeEnd;
    private DateTime? _rangeAnchor;
    private bool _updatingDates;
    private bool _updatingLayout;
    private bool _liveRange;
    private bool _showingTrend;
    private bool _trendRenderPending;
    private bool _trendRangeValid;
    private long _maximum = 1;
    private long _trendAxisMaximum = 1;
    private DateOnly _trendStart;
    private DateOnly _trendEnd;
    private DateOnly _todayAtRefresh;

    public KeyboardStatisticsWindow(
        DesktopPetRepository repository,
        KeyboardStatisticsService keyboardStatistics,
        string keyboardLayoutId,
        Action<string> saveLayout)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _flushPending = keyboardStatistics.Flush;
        _saveLayout = saveLayout;
        keyboardStatistics.KeyPressed += OnKeyPressed;
        Closed += (_, _) => keyboardStatistics.KeyPressed -= OnKeyPressed;
        RangeCalendar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(CalendarPreviewMouseDown), handledEventsToo: true);
        _updatingLayout = true;
        LayoutBox.ItemsSource = KeyboardLayoutCatalog.All;
        LayoutBox.SelectedItem = KeyboardLayoutCatalog.Find(keyboardLayoutId);
        _updatingLayout = false;
        BuildKeyboard((KeyboardLayoutDefinition)LayoutBox.SelectedItem);
        UpdateViewState();
        RangeBox.SelectedIndex = 0;
    }

    public void RefreshStatistics()
    {
        _flushPending();
        _datesWithData = _repository.GetKeyboardStatisticDates().ToHashSet();
        var (start, end) = GetSelectedRange();
        _counts = _repository.GetKeyboardStatistics(start, end);
        _dailyTotals = _repository.GetKeyboardDailyTotals(start, end);
        _liveRange = RangeIncludesToday(start, end);
        _liveIncrements.Clear();
        _todayAtRefresh = DateOnly.FromDateTime(DateTime.Now);
        SetTrendRange(start, end);
        ApplyCounts();
        RenderTrend();
    }

    private static bool RangeIncludesToday(DateOnly? start, DateOnly? end)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (start is { } from && today < from) return false;
        if (end is { } to && today > to) return false;
        return true;
    }

    private void ApplyCounts()
    {
        _maximum = RecalculateMaximum();
        foreach (var (keyCode, views) in _keyViews)
        {
            var count = DisplayCount(keyCode);
            foreach (var view in views)
            {
                view.Background = HeatBrush(count, _maximum);
                SetCountText(view, count);
            }
        }
        TotalCountText.Text = $"合计 {DisplayTotal():N0} 次";
    }

    private void OnKeyPressed(object? sender, int keyCode)
    {
        if (!IsVisible) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _todayAtRefresh)
        {
            RefreshStatistics();
            if (!_showingTrend && _liveRange && _keyViews.TryGetValue(keyCode, out var refreshedViews)) FlashKey(refreshedViews);
            return;
        }
        if (!_liveRange) return;

        _liveIncrements[keyCode] = _liveIncrements.GetValueOrDefault(keyCode) + 1;
        TotalCountText.Text = $"合计 {DisplayTotal():N0} 次";
        if (_showingTrend)
        {
            RequestTrendRender();
            return;
        }

        if (!_keyViews.TryGetValue(keyCode, out var views)) return;
        var maximum = RecalculateMaximum();
        if (maximum != _maximum)
        {
            _maximum = maximum;
            ApplyCounts();
        }
        else
        {
            var count = DisplayCount(keyCode);
            foreach (var view in views)
            {
                view.Background = HeatBrush(count, _maximum);
                SetCountText(view, count);
            }
        }
        FlashKey(views);
    }

    private long RecalculateMaximum()
    {
        var maximum = 1L;
        foreach (var keyCode in _keyViews.Keys)
        {
            var count = DisplayCount(keyCode);
            if (count > maximum) maximum = count;
        }
        return maximum;
    }

    private long DisplayCount(int keyCode) => _counts.GetValueOrDefault(keyCode) + _liveIncrements.GetValueOrDefault(keyCode);

    private long DisplayTotal() => _counts.Values.Sum() + _liveIncrements.Values.Sum();

    private static void SetCountText(Border view, long count) => ((TextBlock)((StackPanel)view.Child).Children[1]).Text = count.ToString();

    private static void FlashKey(IReadOnlyList<Border> views)
    {
        foreach (var view in views)
        {
            if (view.Background is SolidColorBrush brush)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
                {
                    From = LiveFlashColor,
                    To = brush.Color,
                    Duration = TimeSpan.FromMilliseconds(280),
                    FillBehavior = FillBehavior.Stop
                });
            }
            if (view.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, CreateGrowAnimation());
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, CreateGrowAnimation());
            }
        }
    }

    private static DoubleAnimation CreateGrowAnimation() => new()
    {
        From = 1,
        To = 1.15,
        Duration = TimeSpan.FromMilliseconds(80),
        AutoReverse = true,
        FillBehavior = FillBehavior.Stop
    };

    private void ShowDistributionView(object sender, RoutedEventArgs e)
    {
        _showingTrend = false;
        UpdateViewState();
    }

    private void ShowTrendView(object sender, RoutedEventArgs e)
    {
        _showingTrend = true;
        UpdateViewState();
        RequestTrendRender();
    }

    private void UpdateViewState()
    {
        KeyboardGrid.Visibility = _showingTrend ? Visibility.Collapsed : Visibility.Visible;
        TrendHost.Visibility = _showingTrend ? Visibility.Visible : Visibility.Collapsed;
        LayoutBox.Visibility = _showingTrend ? Visibility.Collapsed : Visibility.Visible;
        SetViewButtonAppearance(DistributionViewButton, !_showingTrend);
        SetViewButtonAppearance(TrendViewButton, _showingTrend);
        if (!_showingTrend) ApplyCounts();
    }

    private static void SetViewButtonAppearance(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? AccentColor : Color.FromRgb(244, 239, 248));
        button.Foreground = new SolidColorBrush(selected ? Colors.White : Color.FromRgb(101, 86, 127));
        button.BorderBrush = new SolidColorBrush(selected ? AccentColor : Color.FromRgb(205, 191, 224));
    }

    private void LayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLayout || LayoutBox.SelectedItem is not KeyboardLayoutDefinition layout) return;
        BuildKeyboard(layout);
        ApplyCounts();
        _saveLayout(layout.Id);
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
        if (_dailyTotals.Count > 0)
        {
            _trendStart = _dailyTotals.Keys.Min();
            _trendEnd = _dailyTotals.Keys.Max();
        }
        else
        {
            _trendStart = _todayAtRefresh;
            _trendEnd = _todayAtRefresh;
        }
    }

    private long DailyCount(DateOnly date)
    {
        var count = _dailyTotals.GetValueOrDefault(date);
        if (date == _todayAtRefresh) count += _liveIncrements.Values.Sum();
        return count;
    }

    private bool HasTrendData() => _dailyTotals.Values.Any(value => value > 0) || _liveIncrements.Values.Sum() > 0;

    private void TrendHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_showingTrend) RenderTrend();
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
        if (width < PlotLeft + PlotRight + 20 || height < PlotTop + PlotBottom + 20) return;
        if (!_trendRangeValid || !HasTrendData())
        {
            var empty = new TextBlock
            {
                Text = "该范围暂无按键记录",
                Foreground = MutedBrush,
                FontSize = 13,
                Width = width,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetTop(empty, Math.Max(0, height / 2 - 10));
            TrendCanvas.Children.Add(empty);
            return;
        }

        var plotWidth = width - PlotLeft - PlotRight;
        var plotHeight = height - PlotTop - PlotBottom;
        var maxValue = Math.Max(1, _dailyTotals.Values.DefaultIfEmpty(0).Max());
        if (_liveRange) maxValue = Math.Max(maxValue, DailyCount(_todayAtRefresh));
        _trendAxisMaximum = NiceAxisMaximum(maxValue);

        const int yDivisions = 4;
        for (var index = 0; index <= yDivisions; index++)
        {
            var y = PlotTop + plotHeight - index * plotHeight / yDivisions;
            TrendCanvas.Children.Add(new Line { X1 = PlotLeft, X2 = width - PlotRight, Y1 = y, Y2 = y, Stroke = GridBrush, StrokeThickness = 1 });
            var value = (long)Math.Round(_trendAxisMaximum * index / (double)yDivisions);
            var label = new TextBlock { Text = FormatAxisValue(value), Foreground = MutedBrush, FontSize = 10, Width = PlotLeft - 8, TextAlignment = TextAlignment.Right };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 7);
            TrendCanvas.Children.Add(label);
        }

        foreach (var tick in GetHorizontalTicks(plotWidth))
        {
            var x = DateX(tick, plotWidth);
            TrendCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = PlotTop, Y2 = PlotTop + plotHeight, Stroke = GridBrush, StrokeThickness = 1, Opacity = .65 });
            var label = new TextBlock { Text = FormatHorizontalTick(tick), Foreground = MutedBrush, FontSize = 10, Width = 68, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(label, Math.Clamp(x - 34, 0, Math.Max(0, width - 68)));
            Canvas.SetTop(label, PlotTop + plotHeight + 7);
            TrendCanvas.Children.Add(label);
        }

        var series = BuildTrendSeries();
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(AccentColor),
            StrokeThickness = 2.25,
            StrokeLineJoin = PenLineJoin.Round
        };
        foreach (var (date, count) in series)
            polyline.Points.Add(new Point(DateX(date, plotWidth), ValueY(count, plotHeight)));
        TrendCanvas.Children.Add(polyline);

        if (_trendStart == _trendEnd)
        {
            var count = DailyCount(_trendStart);
            var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(AccentColor) };
            Canvas.SetLeft(dot, DateX(_trendStart, plotWidth) - 4);
            Canvas.SetTop(dot, ValueY(count, plotHeight) - 4);
            TrendCanvas.Children.Add(dot);
        }
    }

    private IReadOnlyList<(DateOnly Date, long Count)> BuildTrendSeries()
    {
        var values = new SortedDictionary<DateOnly, long>();
        foreach (var (date, count) in _dailyTotals)
            if (date >= _trendStart && date <= _trendEnd && count > 0) values[date] = count;
        if (_todayAtRefresh >= _trendStart && _todayAtRefresh <= _trendEnd)
        {
            var live = _liveIncrements.Values.Sum();
            if (live > 0) values[_todayAtRefresh] = values.GetValueOrDefault(_todayAtRefresh) + live;
        }

        var series = new List<(DateOnly Date, long Count)>();
        AddSeriesPoint(series, _trendStart, values.GetValueOrDefault(_trendStart));
        var previous = _trendStart;
        foreach (var (date, count) in values)
        {
            if (date < _trendStart || date > _trendEnd) continue;
            var gap = date.DayNumber - previous.DayNumber;
            if (gap > 1) AddSeriesPoint(series, previous.AddDays(1), 0);
            if (gap > 2) AddSeriesPoint(series, date.AddDays(-1), 0);
            AddSeriesPoint(series, date, count);
            previous = date;
        }
        var endGap = _trendEnd.DayNumber - previous.DayNumber;
        if (endGap > 1) AddSeriesPoint(series, previous.AddDays(1), 0);
        AddSeriesPoint(series, _trendEnd, values.GetValueOrDefault(_trendEnd));
        return series;
    }

    private static void AddSeriesPoint(List<(DateOnly Date, long Count)> series, DateOnly date, long count)
    {
        if (series.Count > 0 && series[^1].Date == date) series[^1] = (date, count);
        else series.Add((date, count));
    }

    private IEnumerable<DateOnly> GetHorizontalTicks(double plotWidth)
    {
        var spanDays = _trendEnd.DayNumber - _trendStart.DayNumber + 1;
        if (spanDays <= 31)
        {
            var step = ChooseInterval(Math.Ceiling(spanDays * 52 / Math.Max(1, plotWidth)), [1, 2, 3, 5, 7, 10, 14]);
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
            var step = ChooseInterval(Math.Ceiling(weeks * 58 / Math.Max(1, plotWidth)), [1, 2, 4, 8]);
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
        var monthStep = ChooseInterval(Math.Ceiling(months * 66 / Math.Max(1, plotWidth)), [1, 2, 3, 6, 12, 24, 36, 60]);
        for (var date = firstMonth; date <= _trendEnd;)
        {
            yield return date;
            var remainingMonths = (_trendEnd.Year - date.Year) * 12 + _trendEnd.Month - date.Month;
            if (remainingMonths < monthStep) break;
            date = date.AddMonths(monthStep);
        }
    }

    private string FormatHorizontalTick(DateOnly date)
    {
        var spanDays = _trendEnd.DayNumber - _trendStart.DayNumber + 1;
        if (spanDays <= 180) return date.ToString("MM-dd");
        return _trendStart.Year == _trendEnd.Year ? date.ToString("MM月") : date.ToString("yyyy-MM");
    }

    private static int ChooseInterval(double minimum, IReadOnlyList<int> candidates)
    {
        foreach (var candidate in candidates) if (candidate >= minimum) return candidate;
        var largest = candidates[^1];
        return Math.Max(largest, (int)Math.Ceiling(minimum / largest) * largest);
    }

    private static long NiceAxisMaximum(long maximum)
    {
        if (maximum <= 4) return 4;
        var roughStep = maximum / 4d;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var step = nice * magnitude;
        return (long)Math.Ceiling(maximum / step) * (long)step;
    }

    private static string FormatAxisValue(long value) => value switch
    {
        >= 100_000_000 => $"{value / 100_000_000d:0.#}亿",
        >= 10_000 => $"{value / 10_000d:0.#}万",
        _ => value.ToString("N0")
    };

    private double DateX(DateOnly date, double plotWidth)
    {
        var span = _trendEnd.DayNumber - _trendStart.DayNumber;
        return span == 0 ? PlotLeft + plotWidth / 2 : PlotLeft + (date.DayNumber - _trendStart.DayNumber) * plotWidth / span;
    }

    private double ValueY(long value, double plotHeight) => PlotTop + plotHeight * (1 - value / (double)Math.Max(1, _trendAxisMaximum));

    private void TrendMouseMove(object sender, MouseEventArgs e)
    {
        TrendHoverCanvas.Children.Clear();
        if (!_showingTrend || !_trendRangeValid || !HasTrendData()) return;
        var width = TrendHoverCanvas.ActualWidth;
        var height = TrendHoverCanvas.ActualHeight;
        var plotWidth = width - PlotLeft - PlotRight;
        var plotHeight = height - PlotTop - PlotBottom;
        if (plotWidth <= 0 || plotHeight <= 0) return;
        var pointer = e.GetPosition(TrendHoverCanvas);
        if (pointer.X < PlotLeft || pointer.X > PlotLeft + plotWidth || pointer.Y < PlotTop || pointer.Y > PlotTop + plotHeight) return;

        var span = _trendEnd.DayNumber - _trendStart.DayNumber;
        var dayOffset = span == 0 ? 0 : (int)Math.Round((pointer.X - PlotLeft) / plotWidth * span);
        var date = _trendStart.AddDays(Math.Clamp(dayOffset, 0, span));
        var count = DailyCount(date);
        var x = DateX(date, plotWidth);
        var y = ValueY(count, plotHeight);
        TrendHoverCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = PlotTop, Y2 = PlotTop + plotHeight, Stroke = new SolidColorBrush(AccentColor), StrokeThickness = 1, Opacity = .65 });
        var dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(AccentColor), Stroke = Brushes.White, StrokeThickness = 1.5 };
        Canvas.SetLeft(dot, x - 4);
        Canvas.SetTop(dot, y - 4);
        TrendHoverCanvas.Children.Add(dot);

        var text = new TextBlock { Text = $"{date:yyyy-MM-dd}  {count:N0} 次", Foreground = new SolidColorBrush(Color.FromRgb(101, 86, 127)), FontSize = 11 };
        var tooltip = new Border { Padding = new Thickness(7, 4, 7, 4), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(252, 250, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(205, 191, 224)), BorderThickness = new Thickness(1), Child = text };
        tooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(x + 8, 2, Math.Max(2, width - tooltip.DesiredSize.Width - 2));
        var top = Math.Clamp(y - tooltip.DesiredSize.Height - 8, 2, Math.Max(2, height - tooltip.DesiredSize.Height - 2));
        Canvas.SetLeft(tooltip, left);
        Canvas.SetTop(tooltip, top);
        TrendHoverCanvas.Children.Add(tooltip);
    }

    private void TrendMouseLeave(object sender, MouseEventArgs e) => TrendHoverCanvas.Children.Clear();

    private void RangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updatingDates) return;
        if (RangeBox.SelectedIndex != 4) SetPresetDates();
        RefreshStatistics();
    }

    private void SetPresetDates()
    {
        _flushPending();
        _datesWithData = _repository.GetKeyboardStatisticDates().ToHashSet();
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
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(141, 122, 184));
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

    private void BuildKeyboard(KeyboardLayoutDefinition layout)
    {
        KeyboardGrid.Children.Clear();
        KeyboardGrid.ColumnDefinitions.Clear();
        _keyViews.Clear();

        var column = 0;
        foreach (var section in layout.Sections)
        {
            if (section.GapBefore > 0)
            {
                KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(section.GapBefore) });
                column++;
            }
            KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(section.WidthInKeys * KeyWidth) });
            AddSection(column, section.Rows);
            column++;
        }
    }

    private void AddSection(int column, IReadOnlyList<KeyboardKeySpec[]> rows)
    {
        var panel = new StackPanel();
        Grid.SetColumn(panel, column);
        KeyboardGrid.Children.Add(panel);
        foreach (var row in rows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Height = 42 };
            panel.Children.Add(rowPanel);
            foreach (var key in row)
            {
                if (key.KeyCode is null) { rowPanel.Children.Add(new Border { Width = KeyWidth * key.Width }); continue; }
                var countText = new TextBlock { FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(101,86,127)), HorizontalAlignment = HorizontalAlignment.Center };
                var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock { Text = key.Label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
                content.Children.Add(countText);
                var border = new Border { Width = KeyWidth * key.Width - 3, Height = 39, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(5), BorderBrush = new SolidColorBrush(Color.FromRgb(205,191,224)), BorderThickness = new Thickness(1), Child = content, RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new Point(0.5, 0.5) };
                rowPanel.Children.Add(border);
                if (!_keyViews.TryGetValue(key.KeyCode.Value, out var views)) _keyViews[key.KeyCode.Value] = views = [];
                views.Add(border);
            }
        }
    }

    private static Brush HeatBrush(long count, long maximum)
    {
        var ratio = Math.Sqrt((double)count / maximum);
        return new SolidColorBrush(Color.FromRgb((byte)(246 - 72 * ratio), (byte)(242 - 88 * ratio), (byte)(251 - 40 * ratio)));
    }

}
