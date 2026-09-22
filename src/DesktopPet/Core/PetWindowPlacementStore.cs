using System.Text.Json;

namespace DesktopPet.Core;

public sealed class PetWindowPlacementStore(string path)
{
    public PetWindowSettings? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var settings = JsonSerializer.Deserialize<PetWindowSettingsDto>(File.ReadAllText(path));
            return settings is null ? null : new PetWindowSettings(
                settings.Left,
                settings.Top,
                settings.Width is > 0 and < double.PositiveInfinity ? settings.Width.Value : 256,
                settings.Topmost ?? true,
                settings.Hotkey,
                settings.KeyboardStatisticsHotkey,
                settings.MouseStatisticsHotkey,
                settings.MouseLegendHidden,
                settings.KeyboardLayoutId == "tkl-87" ? "tkl-87" : "full-size-104");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(
        double left,
        double top,
        double width,
        bool topmost,
        HotkeyGesture? hotkey,
        HotkeyGesture? keyboardStatisticsHotkey,
        HotkeyGesture? mouseStatisticsHotkey,
        bool[]? mouseLegendHidden,
        string keyboardLayoutId) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(new PetWindowSettings(left, top, width, topmost, hotkey, keyboardStatisticsHotkey, mouseStatisticsHotkey, mouseLegendHidden, keyboardLayoutId)));
}

public sealed record PetWindowSettings(
    double Left,
    double Top,
    double Width,
    bool Topmost,
    HotkeyGesture? Hotkey,
    HotkeyGesture? KeyboardStatisticsHotkey,
    HotkeyGesture? MouseStatisticsHotkey,
    bool[]? MouseLegendHidden,
    string KeyboardLayoutId);

internal sealed class PetWindowSettingsDto
{
    public double Left { get; init; }
    public double Top { get; init; }
    public double? Width { get; init; }
    public bool? Topmost { get; init; }
    public HotkeyGesture? Hotkey { get; init; }
    public HotkeyGesture? KeyboardStatisticsHotkey { get; init; }
    public HotkeyGesture? MouseStatisticsHotkey { get; init; }
    public bool[]? MouseLegendHidden { get; init; }
    public string? KeyboardLayoutId { get; init; }
}
