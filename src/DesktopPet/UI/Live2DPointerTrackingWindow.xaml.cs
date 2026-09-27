using DesktopPet.Core;
using DesktopPet.Pet;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace DesktopPet.UI;

public partial class Live2DPointerTrackingWindow : Window
{
    private readonly List<PointerTrackingRow> _rows = [];

    public IReadOnlyList<Live2DPointerTrackingSetting> PointerTracking { get; private set; } = [];

    public Live2DPointerTrackingWindow(IReadOnlyList<Live2DPointerTrackingSetting> settings)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        SourceColumn.ItemsSource = new[]
        {
            new PointerSourceOption(string.Empty, "未设置"),
            new PointerSourceOption(Live2DPointerTracking.MouseLeftX, "鼠标左键X"),
            new PointerSourceOption(Live2DPointerTracking.MouseLeftY, "鼠标左键Y")
        };
        SourceColumn.SelectedValuePath = nameof(PointerSourceOption.Value);
        SourceColumn.DisplayMemberPath = nameof(PointerSourceOption.Label);
        ApplySettings(settings);
    }

    private void ApplySettings(IReadOnlyList<Live2DPointerTrackingSetting> settings)
    {
        var byId = settings.ToDictionary(setting => setting.ParameterId, StringComparer.Ordinal);
        _rows.Clear();
        foreach (var parameter in Live2DPointerTracking.Parameters)
        {
            byId.TryGetValue(parameter.ParameterId, out var setting);
            _rows.Add(new PointerTrackingRow(
                parameter.Name,
                parameter.ParameterId,
                setting is null ? string.Empty : setting.Impact.ToString("0.##", CultureInfo.InvariantCulture),
                setting?.Reflect ?? false,
                setting?.Source ?? string.Empty));
        }
        TrackingGrid.ItemsSource = null;
        TrackingGrid.ItemsSource = _rows;
    }

    private void RestoreDefaults(object sender, RoutedEventArgs e) => ApplySettings(Live2DPointerTracking.CreateDefaults());
    private void ClearAll(object sender, RoutedEventArgs e) => ApplySettings([]);

    private void Save(object sender, RoutedEventArgs e)
    {
        TrackingGrid.CommitEdit();
        var result = new List<Live2DPointerTrackingSetting>();
        try
        {
            foreach (var row in _rows)
            {
                if (string.IsNullOrEmpty(row.Source)) continue;
                if (!double.TryParse(row.ImpactText, NumberStyles.Float, CultureInfo.InvariantCulture, out var impact) ||
                    !double.IsFinite(impact) || impact is < 0 or > 100)
                    throw new InvalidDataException($"{row.Name} 的影响度必须为 0 到 100，请使用小数点格式。");
                result.Add(new Live2DPointerTrackingSetting(row.ParameterId, impact, row.Reflect, row.Source));
            }
            PointerTracking = result;
            DialogResult = true;
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存鼠标跟踪设置");
        }
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DragEditorWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    private sealed class PointerTrackingRow(string name, string parameterId, string impactText, bool reflect, string source)
    {
        public string Name { get; } = name;
        public string ParameterId { get; } = parameterId;
        public string ImpactText { get; set; } = impactText;
        public bool Reflect { get; set; } = reflect;
        public string Source { get; set; } = source;
    }

    private sealed record PointerSourceOption(string Value, string Label);
}
