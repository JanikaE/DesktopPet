namespace DesktopPet.Core;

public static class AppPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPet");

    public static string PetAssetsDirectory => Path.Combine(Root, "assets", "pet");
    public static string PetPackagesDirectory => Path.Combine(PetAssetsDirectory, "packages");
    public static string LogsDirectory => Path.Combine(Root, "logs");
    public static string WindowPlacementPath => Path.Combine(Root, "settings.json");
    public static string DatabasePath => Path.Combine(Root, "desktop-pet.db");
    public static string ShortcutsDirectory => Path.Combine(Root, "shortcuts");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(PetAssetsDirectory);
        Directory.CreateDirectory(PetPackagesDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ShortcutsDirectory);
    }
}
