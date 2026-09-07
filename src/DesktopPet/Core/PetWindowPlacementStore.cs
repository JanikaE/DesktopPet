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
            return settings is null ? null : new PetWindowSettings(settings.Left, settings.Top, settings.Topmost ?? true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(double left, double top, bool topmost) => File.WriteAllText(
        path,
        JsonSerializer.Serialize(new PetWindowSettings(left, top, topmost)));
}

public sealed record PetWindowSettings(double Left, double Top, bool Topmost);

internal sealed class PetWindowSettingsDto
{
    public double Left { get; init; }
    public double Top { get; init; }
    public bool? Topmost { get; init; }
}
