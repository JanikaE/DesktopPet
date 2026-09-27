using DesktopPet.Core;
using DesktopPet.Pet;
using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace DesktopPet.UI;

public partial class Live2DImportWindow : Window
{
    private Live2DModelInspection? _inspection;
    private IReadOnlyList<Live2DPointerTrackingSetting> _pointerTracking = Live2DPointerTracking.CreateDefaults();

    public string PetName => NameBox.Text.Trim();
    public string ModelPath { get; private set; } = string.Empty;
    public double ModelScale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public string? IdleMotion => SelectedMotion(IdleMotionBox.SelectedItem);
    public string? ClickMotion => SelectedMotion(ClickMotionBox.SelectedItem);
    public string? DraggingMotion => SelectedMotion(DraggingMotionBox.SelectedItem);
    public IReadOnlyList<Live2DPointerTrackingSetting> PointerTracking => _pointerTracking;

    public Live2DImportWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        SetMotionGroups([]);
    }

    private void ConfigurePointerTracking(object sender, RoutedEventArgs e)
    {
        var editor = new Live2DPointerTrackingWindow(_pointerTracking) { Owner = this };
        if (editor.ShowDialog() == true) _pointerTracking = editor.PointerTracking;
    }

    private void ChooseModel(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Live2D 模型 (*.model3.json;*.moc3;*.cmo3)|*.model3.json;*.moc3;*.cmo3|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _inspection = PetCatalog.InspectLive2DModel(dialog.FileName);
            ModelPath = _inspection.ModelPath.Length == 0
                ? dialog.FileName
                : Path.Combine(_inspection.SourceDirectory, _inspection.ModelPath.Replace('/', Path.DirectorySeparatorChar));
            ModelPathBox.Text = ModelPath;
            ModelSummaryText.Text = $"已验证 {_inspection.ResourcePaths.Count} 个资源，共 {FormatBytes(_inspection.TotalBytes)}；动作组 {_inspection.MotionGroups.Count} 个。";
            SetMotionGroups(_inspection.MotionGroups);
            if (NameBox.Text == "Live2D 桌宠") NameBox.Text = Path.GetFileName(dialog.FileName).Replace(".model3.json", string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            _inspection = null;
            ModelPath = string.Empty;
            ModelPathBox.Text = "尚未选择";
            SetMotionGroups([]);
            MessageBox.Show(this, exception.Message, "无法使用 Live2D 模型");
        }
    }

    private void SetMotionGroups(IReadOnlyList<string> groups)
    {
        var values = new[] { "（不指定）" }.Concat(groups).ToArray();
        IdleMotionBox.ItemsSource = values;
        ClickMotionBox.ItemsSource = values;
        DraggingMotionBox.ItemsSource = values;
        IdleMotionBox.SelectedIndex = FindSuggestedGroup(groups, "idle");
        ClickMotionBox.SelectedIndex = FindSuggestedGroup(groups, "tapbody", "click");
        DraggingMotionBox.SelectedIndex = FindSuggestedGroup(groups, "dragging", "drag");
    }

    private static int FindSuggestedGroup(IReadOnlyList<string> groups, params string[] candidates)
    {
        for (var index = 0; index < groups.Count; index++)
            if (candidates.Any(candidate => string.Equals(groups[index], candidate, StringComparison.OrdinalIgnoreCase))) return index + 1;
        return 0;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_inspection is null || string.IsNullOrWhiteSpace(ModelPath)) throw new InvalidDataException("请先选择并验证 model3.json。");
            if (PetName.Length == 0 || PetName.Length > 60) throw new InvalidDataException("桌宠名称必须为 1 到 60 个字符。");
            ModelScale = ParseNumber(ScaleBox.Text, "缩放");
            OffsetX = ParseNumber(OffsetXBox.Text, "水平偏移");
            OffsetY = ParseNumber(OffsetYBox.Text, "垂直偏移");
            if (ModelScale is < 0.1 or > 5) throw new InvalidDataException("缩放必须在 0.1 到 5.0 之间。");
            if (Math.Abs(OffsetX) > 2 || Math.Abs(OffsetY) > 2) throw new InvalidDataException("偏移必须在 -2 到 2 之间。");
            DialogResult = true;
        }
        catch (InvalidDataException exception) { MessageBox.Show(this, exception.Message, "无法导入"); }
    }

    private static double ParseNumber(string text, string label)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidDataException($"{label}不是有效数字，请使用小数点格式。");
        return value;
    }

    private static string? SelectedMotion(object? value) => value is string text && text != "（不指定）" ? text : null;
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.##} MB" : $"{bytes / 1024d:0.##} KB";
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DragEditorWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
