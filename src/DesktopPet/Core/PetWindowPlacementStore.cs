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
                settings.Topmost ?? true,
                settings.Hotkey,
                settings.KeyboardStatisticsHotkey,
                settings.MouseStatisticsHotkey,
                settings.MouseLegendHidden);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(
        double left,
        double top,
        bool topmost,
        HotkeyGesture? hotkey,
        HotkeyGesture? keyboardStatisticsHotkey,
        HotkeyGesture? mouseStatisticsHotkey,
        bool[]? mouseLegendHidden) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(new PetWindowSettings(left, top, topmost, hotkey, keyboardStatisticsHotkey, mouseStatisticsHotkey, mouseLegendHidden)));
}

public sealed record PetWindowSettings(
    double Left,
    double Top,
    bool Topmost,
    HotkeyGesture? Hotkey,
    HotkeyGesture? KeyboardStatisticsHotkey,
    HotkeyGesture? MouseStatisticsHotkey,
    bool[]? MouseLegendHidden);

internal sealed class PetWindowSettingsDto
{
    public double Left { get; init; }
    public double Top { get; init; }
    public bool? Topmost { get; init; }
    public HotkeyGesture? Hotkey { get; init; }
    public HotkeyGesture? KeyboardStatisticsHotkey { get; init; }
    public HotkeyGesture? MouseStatisticsHotkey { get; init; }
    public bool[]? MouseLegendHidden { get; init; }
}
