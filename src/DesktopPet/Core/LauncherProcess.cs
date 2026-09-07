using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DesktopPet.Core;

public static class LauncherProcess
{
    public static void Start(string targetPath)
    {
        var workingDirectory = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory;
        if (Path.GetExtension(targetPath).Equals(".bat", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(targetPath).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var shortcutPath = CreateBatchShortcut(targetPath, workingDirectory);
            Process.Start(new ProcessStartInfo(shortcutPath) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true, WorkingDirectory = workingDirectory });
    }

    private static string CreateBatchShortcut(string batchPath, string workingDirectory)
    {
        Directory.CreateDirectory(AppPaths.ShortcutsDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchPath))).ToLowerInvariant()[..16];
        var shortcutPath = Path.Combine(AppPaths.ShortcutsDirectory, $"batch-{hash}.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Unable to create Windows shortcut.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        shortcut.Arguments = $"/d /c \"{batchPath}\"";
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.WindowStyle = 1;
        shortcut.Description = $"DesktopPet launcher for {Path.GetFileName(batchPath)}";
        shortcut.Save();
        return shortcutPath;
    }
}
