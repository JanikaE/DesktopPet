using System.Runtime.InteropServices;

namespace DesktopPet.Core.Sync;

public static class OneDriveFolderLocator
{
    private static readonly Guid OneDriveKnownFolderId = new("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6");

    public static string? TryGetDefaultSyncDirectory()
    {
        var folderId = OneDriveKnownFolderId;
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var pathPointer) != 0) return null;
        try
        {
            var root = Marshal.PtrToStringUni(pathPointer);
            return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
                ? null
                : Path.Combine(root, "Apps", "DesktopPet", "TodoSync");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
