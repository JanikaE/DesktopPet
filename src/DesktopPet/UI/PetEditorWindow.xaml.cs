using DesktopPet.Core;
using DesktopPet.Pet;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DesktopPet.UI;

public partial class PetEditorWindow : Window
{
    public string PetName => NameBox.Text.Trim();
    public string IdlePath { get; private set; }
    public string? ClickPath { get; private set; }
    public string? DraggingPath { get; private set; }

    public PetEditorWindow(PetDefinition? pet = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        if (pet is null)
        {
            IdlePath = string.Empty;
            NameBox.Text = "自定义桌宠";
        }
        else
        {
            Title = "编辑自定义桌宠";
            TitleText.Text = "编辑自定义桌宠";
            NameBox.Text = pet.Name;
            IdlePath = pet.IdleImagePath;
            ClickPath = pet.StateImages.GetValueOrDefault(PetState.Click);
            DraggingPath = pet.StateImages.GetValueOrDefault(PetState.Dragging);
        }
        RefreshPreviews();
    }

    private void ChoosePng(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string state }) return;
        var dialog = new OpenFileDialog { Filter = "PNG 图片 (*.png)|*.png", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { PetCatalog.ValidatePng(dialog.FileName); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "无法使用图片");
            return;
        }
        SetPath(state, dialog.FileName);
        RefreshPreviews();
    }

    private void ClearPng(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string state }) SetPath(state, null);
        RefreshPreviews();
    }

    private void SetPath(string state, string? path)
    {
        switch (state)
        {
            case "Idle": IdlePath = path ?? string.Empty; break;
            case "Click": ClickPath = path; break;
            case "Dragging": DraggingPath = path; break;
        }
    }

    private void RefreshPreviews()
    {
        IdlePathText.Text = DisplayPath(IdlePath, "尚未选择");
        ClickPathText.Text = DisplayPath(ClickPath, "使用 Idle");
        DraggingPathText.Text = DisplayPath(DraggingPath, "使用 Idle");
        IdlePreview.Source = LoadPreview(IdlePath);
        ClickPreview.Source = LoadPreview(ClickPath) ?? IdlePreview.Source;
        DraggingPreview.Source = LoadPreview(DraggingPath) ?? IdlePreview.Source;
    }

    private static string DisplayPath(string? path, string fallback) => string.IsNullOrWhiteSpace(path) ? fallback : Path.GetFileName(path);

    private static BitmapImage? LoadPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 160;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PetName.Length == 0) throw new InvalidDataException("请输入自定义桌宠名称。");
            if (PetName.Length > 60) throw new InvalidDataException("桌宠名称不能超过 60 个字符。");
            if (string.IsNullOrWhiteSpace(IdlePath)) throw new InvalidDataException("请至少选择一张 Idle PNG。");
            PetCatalog.ValidatePng(IdlePath);
            if (!string.IsNullOrWhiteSpace(ClickPath)) PetCatalog.ValidatePng(ClickPath);
            if (!string.IsNullOrWhiteSpace(DraggingPath)) PetCatalog.ValidatePng(DraggingPath);
            DialogResult = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "无法保存");
        }
    }

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DragEditorWindow(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
