namespace DesktopPet.Core;

public static class ApplicationShortcutService
{
    public static void EnsureDesktopAndStartupShortcuts()
    {
        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable path is unavailable.");
        var workingDirectory = AppContext.BaseDirectory;
        CreateOrUpdate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DesktopPet.lnk"), executablePath, workingDirectory, "DesktopPet");
        CreateOrUpdate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DesktopPet.lnk"), executablePath, workingDirectory, "DesktopPet - 开机启动");
    }

    private static void CreateOrUpdate(string shortcutPath, string executablePath, string workingDirectory, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Unable to create Windows shortcut.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = executablePath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        shortcut.IconLocation = executablePath;
        shortcut.Save();
    }
}
