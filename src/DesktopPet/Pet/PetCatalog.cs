using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DesktopPet.Pet;

public sealed class PetCatalog
{
    public const string BuiltInDefaultId = "builtin.default";
    private const long MaximumPngBytes = 20L * 1024 * 1024;
    private const int MaximumPngDimension = 8192;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private readonly string _builtInDirectory;
    private readonly string _packagesDirectory;

    public PetCatalog(string builtInDirectory, string packagesDirectory)
    {
        _builtInDirectory = Path.GetFullPath(builtInDirectory);
        _packagesDirectory = Path.GetFullPath(packagesDirectory);
        Directory.CreateDirectory(_packagesDirectory);
    }

    public PetDefinition BuiltInDefault => CreateBuiltInDefault();

    public IReadOnlyList<PetDefinition> GetAll()
    {
        var pets = new List<PetDefinition> { CreateBuiltInDefault() };
        foreach (var directory in Directory.EnumerateDirectories(_packagesDirectory))
        {
            if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
            try
            {
                if (LoadPackage(directory) is { } pet) pets.Add(pet);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return pets.OrderByDescending(pet => pet.IsBuiltIn).ThenBy(pet => pet.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public PetDefinition FindOrDefault(string? id) =>
        GetAll().FirstOrDefault(pet => string.Equals(pet.Id, id, StringComparison.Ordinal)) ?? BuiltInDefault;

    public PetDefinition Import(string name, string idlePath, string? clickPath, string? draggingPath)
    {
        var id = Guid.NewGuid().ToString("N");
        return WritePackage(id, name, idlePath, clickPath, draggingPath, replacing: false);
    }

    public PetDefinition Update(string id, string name, string idlePath, string? clickPath, string? draggingPath)
    {
        if (string.Equals(id, BuiltInDefaultId, StringComparison.Ordinal)) throw new InvalidOperationException("内置预设不能编辑。");
        return WritePackage(id, name, idlePath, clickPath, draggingPath, replacing: true);
    }

    public void Delete(string id)
    {
        if (string.Equals(id, BuiltInDefaultId, StringComparison.Ordinal)) throw new InvalidOperationException("内置预设不能删除。");
        var directory = GetPackageDirectory(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public static void ValidatePng(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException("图片文件不存在。");
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumPngBytes) throw new InvalidDataException("PNG 必须小于或等于 20 MB。");

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[PngSignature.Length];
        if (stream.Read(signature) != signature.Length || !signature.SequenceEqual(PngSignature))
            throw new InvalidDataException("所选文件不是有效的 PNG 图片。");
        stream.Position = 0;
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > MaximumPngDimension || frame.PixelHeight > MaximumPngDimension)
                throw new InvalidDataException("PNG 的宽和高不能超过 8192 像素。");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is NotSupportedException or FileFormatException)
        {
            throw new InvalidDataException("PNG 图片无法解码或已经损坏。", exception);
        }
    }

    private PetDefinition CreateBuiltInDefault()
    {
        var idle = Path.Combine(_builtInDirectory, "idle.png");
        ValidatePng(idle);
        var states = new Dictionary<PetState, string> { [PetState.Idle] = idle };
        AddOptionalState(states, PetState.Click, Path.Combine(_builtInDirectory, "click.png"));
        AddOptionalState(states, PetState.Dragging, Path.Combine(_builtInDirectory, "drag.png"));
        return new PetDefinition(BuiltInDefaultId, "默认桌宠", "png", true, _builtInDirectory, states);
    }

    private PetDefinition WritePackage(string id, string name, string idlePath, string? clickPath, string? draggingPath, bool replacing)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("请输入自定义桌宠名称。");
        if (name.Length > 60) throw new InvalidDataException("桌宠名称不能超过 60 个字符。");
        ValidatePng(idlePath);
        if (!string.IsNullOrWhiteSpace(clickPath)) ValidatePng(clickPath);
        if (!string.IsNullOrWhiteSpace(draggingPath)) ValidatePng(draggingPath);

        var finalDirectory = GetPackageDirectory(id);
        if (replacing && !Directory.Exists(finalDirectory)) throw new DirectoryNotFoundException("要编辑的自定义桌宠已经不存在。");
        if (!replacing && Directory.Exists(finalDirectory)) throw new IOException("自定义桌宠目录已经存在。");

        var temporaryDirectory = Path.Combine(_packagesDirectory, $".{id}.{Guid.NewGuid():N}.tmp");
        var backupDirectory = Path.Combine(_packagesDirectory, $".{id}.{Guid.NewGuid():N}.bak");
        Directory.CreateDirectory(temporaryDirectory);
        var movedExisting = false;
        var installedNewPackage = false;
        try
        {
            CopyPng(idlePath, Path.Combine(temporaryDirectory, "idle.png"));
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["idle"] = "idle.png" };
            if (!string.IsNullOrWhiteSpace(clickPath))
            {
                CopyPng(clickPath, Path.Combine(temporaryDirectory, "click.png"));
                states["click"] = "click.png";
            }
            if (!string.IsNullOrWhiteSpace(draggingPath))
            {
                CopyPng(draggingPath, Path.Combine(temporaryDirectory, "dragging.png"));
                states["dragging"] = "dragging.png";
            }

            var manifest = new PetManifest(1, id, name, "png", new PngRendererSettings(states));
            File.WriteAllText(Path.Combine(temporaryDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

            if (replacing)
            {
                Directory.Move(finalDirectory, backupDirectory);
                movedExisting = true;
            }
            Directory.Move(temporaryDirectory, finalDirectory);
            installedNewPackage = true;
            var result = LoadPackage(finalDirectory) ?? throw new InvalidDataException("无法读取刚保存的自定义桌宠。");
            if (movedExisting)
            {
                try { Directory.Delete(backupDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return result;
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            if (installedNewPackage && Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
            if (movedExisting && Directory.Exists(backupDirectory)) Directory.Move(backupDirectory, finalDirectory);
            throw;
        }
    }

    private PetDefinition? LoadPackage(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) return null;
        var manifest = JsonSerializer.Deserialize<PetManifest>(File.ReadAllText(manifestPath), JsonOptions);
        if (manifest is null || manifest.SchemaVersion != 1 || manifest.RendererType != "png" || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name)) return null;
        if (!string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.Ordinal)) return null;
        if (!manifest.RendererSettings.States.TryGetValue("idle", out var idleRelative)) return null;

        var states = new Dictionary<PetState, string>();
        var idle = ResolvePackagePath(directory, idleRelative);
        ValidatePng(idle);
        states[PetState.Idle] = idle;
        AddManifestState(states, PetState.Click, "click", directory, manifest.RendererSettings.States);
        AddManifestState(states, PetState.Dragging, "dragging", directory, manifest.RendererSettings.States);
        return new PetDefinition(manifest.Id, manifest.Name.Trim(), manifest.RendererType, false, directory, states);
    }

    private static void AddManifestState(Dictionary<PetState, string> states, PetState state, string key, string directory, IReadOnlyDictionary<string, string> manifestStates)
    {
        if (!manifestStates.TryGetValue(key, out var relative)) return;
        var path = ResolvePackagePath(directory, relative);
        ValidatePng(path);
        states[state] = path;
    }

    private static string ResolvePackagePath(string directory, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("宠物包清单只能使用相对路径。");
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(directory, relative));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("宠物包清单包含无效路径。");
        return resolved;
    }

    private string GetPackageDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("自定义桌宠 ID 无效。");
        return Path.Combine(_packagesDirectory, id);
    }

    private static void AddOptionalState(Dictionary<PetState, string> states, PetState state, string path)
    {
        if (File.Exists(path)) states[state] = path;
    }

    private static void CopyPng(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return;
        File.Copy(source, destination, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record PetManifest(int SchemaVersion, string Id, string Name, string RendererType, PngRendererSettings RendererSettings);
    private sealed record PngRendererSettings(Dictionary<string, string> States);
}
