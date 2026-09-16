using DesktopPet.Core;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopPet.UI;

public partial class KeyboardStatisticsWindow : Window
{
    private const double KeyWidth = 35;
    private static readonly Color LiveFlashColor = Color.FromRgb(141, 122, 184);
    private readonly DesktopPetRepository _repository;
    private readonly Action _flushPending;
    private readonly Dictionary<int, List<Border>> _keyViews = [];
    private readonly Dictionary<int, long> _liveIncrements = [];
    private IReadOnlyDictionary<int, long> _counts = new Dictionary<int, long>();
    private HashSet<DateOnly> _datesWithData = [];
    private DateTime? _rangeStart;
    private DateTime? _rangeEnd;
    private DateTime? _rangeAnchor;
    private bool _updatingDates;
    private bool _liveRange;
    private long _maximum = 1;

    public KeyboardStatisticsWindow(DesktopPetRepository repository, KeyboardStatisticsService keyboardStatistics)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _flushPending = keyboardStatistics.Flush;
        keyboardStatistics.KeyPressed += OnKeyPressed;
        Closed += (_, _) => keyboardStatistics.KeyPressed -= OnKeyPressed;
        RangeCalendar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(CalendarPreviewMouseDown), handledEventsToo: true);
        BuildKeyboard();
        RangeBox.SelectedIndex = 0;
    }

    public void RefreshStatistics()
    {
        _flushPending();
        _datesWithData = _repository.GetKeyboardStatisticDates().ToHashSet();
        var (start, end) = GetSelectedRange();
        _counts = _repository.GetKeyboardStatistics(start, end);
        _liveRange = RangeIncludesToday(start, end);
        _liveIncrements.Clear();
        ApplyCounts();
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
        if (!IsVisible || !_liveRange) return;
        if (!_keyViews.TryGetValue(keyCode, out var views)) return;
        _liveIncrements[keyCode] = _liveIncrements.GetValueOrDefault(keyCode) + 1;
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
            TotalCountText.Text = $"合计 {DisplayTotal():N0} 次";
        }
        FlashKey(views);
    }

    private long RecalculateMaximum()
    {
        var maximum = 1L;
        var keyCodes = new HashSet<int>(_counts.Keys);
        keyCodes.UnionWith(_liveIncrements.Keys);
        foreach (var keyCode in keyCodes)
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

    private void BuildKeyboard()
    {
        KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15 * KeyWidth) });
        KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3 * KeyWidth) });
        KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        KeyboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4 * KeyWidth) });

        AddSection(0,
            [K("Esc",27), S(1), K("F1",112),K("F2",113),K("F3",114),K("F4",115),S(.5),K("F5",116),K("F6",117),K("F7",118),K("F8",119),S(.5),K("F9",120),K("F10",121),K("F11",122),K("F12",123)],
            [K("`",192),K("1",49),K("2",50),K("3",51),K("4",52),K("5",53),K("6",54),K("7",55),K("8",56),K("9",57),K("0",48),K("-",189),K("=",187),K("Back",8,2)],
            [K("Tab",9,1.5),K("Q",81),K("W",87),K("E",69),K("R",82),K("T",84),K("Y",89),K("U",85),K("I",73),K("O",79),K("P",80),K("[",219),K("]",221),K("\\",220,1.5)],
            [K("Caps",20,1.8),K("A",65),K("S",83),K("D",68),K("F",70),K("G",71),K("H",72),K("J",74),K("K",75),K("L",76),K(";",186),K("'",222),K("Enter",13,2.2)],
            [K("Shift",160,2.3),K("Z",90),K("X",88),K("C",67),K("V",86),K("B",66),K("N",78),K("M",77),K(",",188),K(".",190),K("/",191),K("Shift",161,2.7)],
            [K("Ctrl",162,1.4),K("Win",91,1.2),K("Alt",164,1.2),K("Space",32,6.2),K("Alt",165,1.2),K("Win",92,1.2),K("Menu",93,1.2),K("Ctrl",163,1.4)]);
        AddSection(2,
            [K("Prt",44),K("Scr",145),K("Pause",19)],
            [K("Ins",45),K("Home",36),K("PgUp",33)],
            [K("Del",46),K("End",35),K("PgDn",34)],
            [S(3)],
            [S(1),K("↑",38),S(1)],
            [K("←",37),K("↓",40),K("→",39)]);
        AddSection(4,
            [S(4)],
            [K("Num",144),K("/",111),K("*",106),K("-",109)],
            [K("7",103),K("8",104),K("9",105),K("+",107)],
            [K("4",100),K("5",101),K("6",102),S(1)],
            [K("1",97),K("2",98),K("3",99),K("Enter",0x1000D)],
            [K("0",96,2),K(".",110),S(1)]);
    }

    private void AddSection(int column, params KeySpec[][] rows)
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

    private static KeySpec K(string label, int keyCode, double width = 1) => new(label, keyCode, width);
    private static KeySpec S(double width) => new(string.Empty, null, width);
    private sealed record KeySpec(string Label, int? KeyCode, double Width);
}
