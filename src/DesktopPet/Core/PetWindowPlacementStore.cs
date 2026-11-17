using System.Text.Json;

namespace DesktopPet.Core;

public sealed class PetWindowPlacementStore(string path)
{
    private readonly object _gate = new();

    public PetWindowSettings? Load()
    {
        lock (_gate)
        {
            return Read(path) ?? Read(path + ".bak");
        }
    }

    private static PetWindowSettings? Read(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var settings = JsonSerializer.Deserialize<PetWindowSettingsDto>(File.ReadAllText(filePath));
            return settings is null ? null : new PetWindowSettings(
                settings.Left,
                settings.Top,
                settings.Width is > 0 and < double.PositiveInfinity ? settings.Width.Value : 256,
                settings.Topmost ?? true,
                settings.Hotkey,
                settings.KeyboardStatisticsHotkey,
                settings.MouseStatisticsHotkey,
                settings.MouseLegendHidden,
                settings.KeyboardLayoutId == "tkl-87" ? "tkl-87" : "full-size-104",
                string.IsNullOrWhiteSpace(settings.SelectedPetId) ? Pet.PetCatalog.BuiltInDefaultId : settings.SelectedPetId);
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
        string keyboardLayoutId,
        string selectedPetId)
    {
        // Serialize before touching disk so serialization failures cannot truncate settings.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PetWindowSettings(left, top, width, topmost, hotkey, keyboardStatisticsHotkey, mouseStatisticsHotkey, mouseLegendHidden, keyboardLayoutId, selectedPetId));
        lock (_gate)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(path))
                {
                    // Never replace a good recovery copy with a corrupt primary file.
                    var backupPath = Read(path) is null ? null : path + ".bak";
                    File.Replace(temporaryPath, path, backupPath);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
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
    string KeyboardLayoutId,
    string SelectedPetId);

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
    public string? SelectedPetId { get; init; }
}
