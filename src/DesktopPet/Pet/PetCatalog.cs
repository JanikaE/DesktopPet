using System.Text.Json;
using System.Windows.Media.Imaging;

namespace DesktopPet.Pet;

public sealed class PetCatalog
{
    public const string BuiltInDefaultId = "builtin.default";
    private const long MaximumPngBytes = 20L * 1024 * 1024;
    private const long MaximumLive2DFileBytes = 64L * 1024 * 1024;
    private const long MaximumLive2DPackageBytes = 256L * 1024 * 1024;
    private const int MaximumLive2DFiles = 512;
    private const int MaximumPngDimension = 8192;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly HashSet<string> Live2DExtensions = new(StringComparer.OrdinalIgnoreCase) { ".moc3", ".png", ".json" };
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
            catch (InvalidDataException) { }
        }
        return pets.OrderByDescending(pet => pet.IsBuiltIn).ThenBy(pet => pet.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public PetDefinition FindOrDefault(string? id) =>
        GetAll().FirstOrDefault(pet => string.Equals(pet.Id, id, StringComparison.Ordinal)) ?? BuiltInDefault;

    public PetDefinition Import(string name, string idlePath, string? clickPath, string? draggingPath) =>
        WritePngPackage(Guid.NewGuid().ToString("N"), name, idlePath, clickPath, draggingPath, replacing: false);

    public PetDefinition Update(string id, string name, string idlePath, string? clickPath, string? draggingPath)
    {
        if (string.Equals(id, BuiltInDefaultId, StringComparison.Ordinal)) throw new InvalidOperationException("内置预设不能编辑。");
        return WritePngPackage(id, name, idlePath, clickPath, draggingPath, replacing: true);
    }

    public PetDefinition ImportLive2D(
        string name,
        string modelPath,
        double scale,
        double offsetX,
        double offsetY,
        string? idleMotion,
        string? clickMotion,
        string? draggingMotion,
        IReadOnlyList<Live2DPointerTrackingSetting> pointerTracking)
    {
        var inspection = InspectLive2DModel(modelPath);
        var id = Guid.NewGuid().ToString("N");
        ValidateName(name);
        ValidateTransform(scale, offsetX, offsetY);
        var finalDirectory = GetPackageDirectory(id);
        var temporaryDirectory = Path.Combine(_packagesDirectory, $".{id}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var relativePath in inspection.ResourcePaths)
            {
                var source = ResolvePackagePath(inspection.SourceDirectory, relativePath);
                var destination = ResolvePackagePath(temporaryDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: false);
            }

            var motions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddMotion(motions, "idle", idleMotion, inspection.MotionGroups);
            AddMotion(motions, "click", clickMotion, inspection.MotionGroups);
            AddMotion(motions, "dragging", draggingMotion, inspection.MotionGroups);
            var settings = new Live2DManifestSettings(inspection.ModelPath, null, "contain", scale, offsetX, offsetY, motions, pointerTracking.ToList());
            var manifest = new Live2DManifest(2, id, name.Trim(), "live2d-cubism", settings);
            File.WriteAllText(Path.Combine(temporaryDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));

            ValidateInstalledLive2DPackage(temporaryDirectory, manifest);
            Directory.Move(temporaryDirectory, finalDirectory);
            return LoadPackage(finalDirectory) ?? throw new InvalidDataException("无法读取刚保存的 Live2D 桌宠。");
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
            throw;
        }
    }

    public PetDefinition UpdateLive2DPointerTracking(string id, IReadOnlyList<Live2DPointerTrackingSetting> pointerTracking)
    {
        if (string.Equals(id, BuiltInDefaultId, StringComparison.Ordinal)) throw new InvalidOperationException("内置预设不能编辑。");
        ValidatePointerTracking(pointerTracking);
        var directory = GetPackageDirectory(id);
        var manifestPath = Path.Combine(directory, "manifest.json");
        using var document = ReadJson(manifestPath);
        var manifest = JsonSerializer.Deserialize<Live2DManifest>(document.RootElement.GetRawText(), JsonOptions)
            ?? throw new InvalidDataException("Live2D 宠物包清单无效。");
        var updated = manifest with
        {
            RendererSettings = manifest.RendererSettings with { PointerTracking = pointerTracking.ToList() }
        };
        ValidateInstalledLive2DPackage(directory, updated);
        var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(updated, JsonOptions));
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return LoadPackage(directory) ?? throw new InvalidDataException("无法读取更新后的 Live2D 桌宠。");
    }

    public void Delete(string id)
    {
        if (string.Equals(id, BuiltInDefaultId, StringComparison.Ordinal)) throw new InvalidOperationException("内置预设不能删除。");
        var directory = GetPackageDirectory(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public static Live2DModelInspection InspectLive2DModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("请选择 Live2D 模型文件。");
        path = Path.GetFullPath(path);
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cmo3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CMO3 是 Cubism Editor 工程文件。请在 Cubism Editor 中导出 MOC3，再选择生成的 model3.json。");
        if (extension.Equals(".moc3", StringComparison.OrdinalIgnoreCase))
        {
            var candidates = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.model3.json", SearchOption.TopDirectoryOnly).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("单独的 MOC3 缺少纹理和资源关联。请选择同目录中对应的 model3.json。");
            path = candidates[0];
        }
        if (!path.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("请选择有效的 model3.json 文件。");

        var sourceDirectory = Path.GetDirectoryName(path)!;
        EnsureRegularFile(path);
        using var document = ReadJson(path);
        if (!document.RootElement.TryGetProperty("FileReferences", out var references) || references.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("model3.json 缺少 FileReferences。");

        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetRelativePath(sourceDirectory, path) };
        CollectResourceReferences(references, resources);
        if (!resources.Any(resource => resource.EndsWith(".moc3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("model3.json 没有引用 MOC3 文件。");
        if (!resources.Any(resource => resource.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("model3.json 没有引用纹理 PNG。");
        if (resources.Count > MaximumLive2DFiles) throw new InvalidDataException($"Live2D 包文件数不能超过 {MaximumLive2DFiles} 个。");

        long totalBytes = 0;
        foreach (var relativePath in resources)
        {
            if (Path.IsPathRooted(relativePath) || !Live2DExtensions.Contains(Path.GetExtension(relativePath)))
                throw new InvalidDataException($"Live2D 包包含不支持的资源：{relativePath}");
            var resolved = ResolvePackagePath(sourceDirectory, relativePath);
            EnsureRegularFile(resolved);
            EnsureNoReparsePoints(sourceDirectory, resolved);
            var length = new FileInfo(resolved).Length;
            if (length <= 0 || length > MaximumLive2DFileBytes)
                throw new InvalidDataException($"Live2D 资源大小无效：{relativePath}");
            totalBytes = checked(totalBytes + length);
            if (totalBytes > MaximumLive2DPackageBytes) throw new InvalidDataException("Live2D 包总大小不能超过 256 MB。");
            if (relativePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ValidateTexturePng(resolved);
            if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) using (ReadJson(resolved)) { }
            if (relativePath.EndsWith(".moc3", StringComparison.OrdinalIgnoreCase)) ValidateMoc3(resolved);
        }

        var motionGroups = new List<string>();
        if (references.TryGetProperty("Motions", out var motions) && motions.ValueKind == JsonValueKind.Object)
            motionGroups.AddRange(motions.EnumerateObject().Select(group => group.Name));

        return new Live2DModelInspection(
            Path.GetRelativePath(sourceDirectory, path).Replace(Path.DirectorySeparatorChar, '/'),
            sourceDirectory,
            resources.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            motionGroups.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            totalBytes);
    }

    public static void ValidatePng(string path) => ValidatePng(path, MaximumPngBytes);
    private static void ValidateTexturePng(string path) => ValidatePng(path, MaximumLive2DFileBytes);

    private static void ValidatePng(string path, long maximumBytes)
    {
        if (!File.Exists(path)) throw new InvalidDataException("图片文件不存在。");
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > maximumBytes) throw new InvalidDataException($"PNG 必须小于或等于 {maximumBytes / 1024 / 1024} MB。");
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

    private PetDefinition WritePngPackage(string id, string name, string idlePath, string? clickPath, string? draggingPath, bool replacing)
    {
        ValidateName(name);
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
            if (!string.IsNullOrWhiteSpace(clickPath)) { CopyPng(clickPath, Path.Combine(temporaryDirectory, "click.png")); states["click"] = "click.png"; }
            if (!string.IsNullOrWhiteSpace(draggingPath)) { CopyPng(draggingPath, Path.Combine(temporaryDirectory, "dragging.png")); states["dragging"] = "dragging.png"; }
            var manifest = new PngManifest(1, id, name.Trim(), "png", new PngManifestSettings(states));
            File.WriteAllText(Path.Combine(temporaryDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            if (replacing) { Directory.Move(finalDirectory, backupDirectory); movedExisting = true; }
            Directory.Move(temporaryDirectory, finalDirectory);
            installedNewPackage = true;
            var result = LoadPackage(finalDirectory) ?? throw new InvalidDataException("无法读取刚保存的自定义桌宠。");
            if (movedExisting) TryDeleteDirectory(backupDirectory);
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
        using var document = ReadJson(manifestPath);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schemaProperty) || !root.TryGetProperty("id", out var idProperty) ||
            !root.TryGetProperty("name", out var nameProperty) || !root.TryGetProperty("rendererType", out var rendererTypeProperty) ||
            !root.TryGetProperty("rendererSettings", out var settingsProperty)) return null;
        var schemaVersion = schemaProperty.GetInt32();
        var id = idProperty.GetString();
        var name = nameProperty.GetString();
        var rendererType = rendererTypeProperty.GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rendererType)) return null;
        if (!string.Equals(Path.GetFileName(directory), id, StringComparison.Ordinal)) return null;
        return rendererType switch
        {
            "png" when schemaVersion == 1 => LoadPngPackage(directory, id, name, rendererType, settingsProperty),
            "live2d-cubism" when schemaVersion == 2 => LoadLive2DPackage(directory, id, name, rendererType, settingsProperty),
            _ => null
        };
    }

    private static PetDefinition? LoadPngPackage(string directory, string id, string name, string rendererType, JsonElement settings)
    {
        if (!settings.TryGetProperty("states", out var manifestStates) || manifestStates.ValueKind != JsonValueKind.Object ||
            !manifestStates.TryGetProperty("idle", out var idleProperty) || idleProperty.GetString() is not { } idleRelative) return null;
        var states = new Dictionary<PetState, string>();
        var idle = ResolvePackagePath(directory, idleRelative);
        ValidatePng(idle);
        states[PetState.Idle] = idle;
        AddManifestState(states, PetState.Click, "click", directory, manifestStates);
        AddManifestState(states, PetState.Dragging, "dragging", directory, manifestStates);
        return new PetDefinition(id, name.Trim(), rendererType, false, directory, states);
    }

    private static PetDefinition? LoadLive2DPackage(string directory, string id, string name, string rendererType, JsonElement settings)
    {
        var manifest = JsonSerializer.Deserialize<Live2DManifestSettings>(settings.GetRawText(), JsonOptions);
        if (manifest is null) return null;
        ValidateInstalledLive2DPackage(directory, new Live2DManifest(2, id, name, rendererType, manifest));
        var motions = new Dictionary<PetState, string>();
        AddLoadedMotion(motions, PetState.Idle, "idle", manifest.Motions);
        AddLoadedMotion(motions, PetState.Click, "click", manifest.Motions);
        AddLoadedMotion(motions, PetState.Dragging, "dragging", manifest.Motions);
        var modelPath = ResolvePackagePath(directory, manifest.Model);
        var thumbnail = string.IsNullOrWhiteSpace(manifest.Thumbnail) ? null : ResolvePackagePath(directory, manifest.Thumbnail);
        var pointerTracking = manifest.PointerTracking ?? Live2DPointerTracking.CreateDefaults().ToList();
        var live2D = new Live2DRendererSettings(modelPath, thumbnail, manifest.Fit, manifest.Scale, manifest.OffsetX, manifest.OffsetY, motions, pointerTracking);
        return new PetDefinition(id, name.Trim(), rendererType, false, directory, new Dictionary<PetState, string>(), live2D);
    }

    private static void ValidateInstalledLive2DPackage(string directory, Live2DManifest manifest)
    {
        if (!string.Equals(manifest.RendererType, "live2d-cubism", StringComparison.Ordinal) || manifest.SchemaVersion != 2)
            throw new InvalidDataException("Live2D 宠物包清单版本无效。");
        ValidateName(manifest.Name);
        ValidateTransform(manifest.RendererSettings.Scale, manifest.RendererSettings.OffsetX, manifest.RendererSettings.OffsetY);
        if (!string.Equals(manifest.RendererSettings.Fit, "contain", StringComparison.Ordinal)) throw new InvalidDataException("Live2D 桌宠只支持 contain 适配模式。");
        var inspection = InspectLive2DModel(ResolvePackagePath(directory, manifest.RendererSettings.Model));
        foreach (var motion in manifest.RendererSettings.Motions.Values)
            if (!inspection.MotionGroups.Contains(motion, StringComparer.Ordinal)) throw new InvalidDataException($"Live2D 动作组不存在：{motion}");
        ValidatePointerTracking(manifest.RendererSettings.PointerTracking ?? Live2DPointerTracking.CreateDefaults());
        if (!string.IsNullOrWhiteSpace(manifest.RendererSettings.Thumbnail)) ValidatePng(ResolvePackagePath(directory, manifest.RendererSettings.Thumbnail));
    }

    private static void ValidatePointerTracking(IReadOnlyList<Live2DPointerTrackingSetting> settings)
    {
        var supportedIds = Live2DPointerTracking.Parameters.Select(parameter => parameter.ParameterId).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in settings)
        {
            if (!supportedIds.Contains(setting.ParameterId) || !seen.Add(setting.ParameterId))
                throw new InvalidDataException($"Live2D 鼠标追踪参数无效：{setting.ParameterId}");
            if (!double.IsFinite(setting.Impact) || setting.Impact is < 0 or > 100)
                throw new InvalidDataException($"{setting.ParameterId} 的鼠标追踪影响度必须为 0 到 100。");
            if (setting.Source is not Live2DPointerTracking.MouseLeftX and not Live2DPointerTracking.MouseLeftY)
                throw new InvalidDataException($"{setting.ParameterId} 的鼠标追踪类型无效。");
        }
    }

    private static void CollectResourceReferences(JsonElement element, HashSet<string> resources)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectResourceReferences(property.Value, resources);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectResourceReferences(item, resources);
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value) && Live2DExtensions.Contains(Path.GetExtension(value))) resources.Add(value.Replace('/', Path.DirectorySeparatorChar));
                break;
        }
    }

    private static JsonDocument ReadJson(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumLive2DFileBytes) throw new InvalidDataException($"JSON 文件大小无效：{Path.GetFileName(path)}");
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 64, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception) { throw new InvalidDataException($"JSON 文件无法解析：{Path.GetFileName(path)}", exception); }
    }

    private static void EnsureRegularFile(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"Live2D 资源不存在：{Path.GetFileName(path)}");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Live2D 资源不能是链接或重解析点：{Path.GetFileName(path)}");
    }

    private static void EnsureNoReparsePoints(string root, string path)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = Path.GetDirectoryName(Path.GetFullPath(path)); current is not null && current.Length >= rootPath.Length; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Live2D 资源路径不能经过链接或重解析点：{Path.GetFileName(path)}");
            if (string.Equals(current, rootPath, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private static void ValidateMoc3(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length || !signature.SequenceEqual("MOC3"u8))
            throw new InvalidDataException($"MOC3 文件签名无效：{Path.GetFileName(path)}");
    }

    private static void ValidateName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidDataException("请输入自定义桌宠名称。");
        if (name.Length > 60) throw new InvalidDataException("桌宠名称不能超过 60 个字符。");
    }

    private static void ValidateTransform(double scale, double offsetX, double offsetY)
    {
        if (!double.IsFinite(scale) || scale is < 0.1 or > 5.0) throw new InvalidDataException("Live2D 缩放必须在 0.1 到 5.0 之间。");
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY) || Math.Abs(offsetX) > 2 || Math.Abs(offsetY) > 2) throw new InvalidDataException("Live2D 偏移必须在 -2 到 2 之间。");
    }

    private static void AddMotion(Dictionary<string, string> target, string key, string? value, IReadOnlyList<string> available)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!available.Contains(value, StringComparer.Ordinal)) throw new InvalidDataException($"Live2D 动作组不存在：{value}");
        target[key] = value;
    }

    private static void AddLoadedMotion(Dictionary<PetState, string> target, PetState state, string key, IReadOnlyDictionary<string, string> source)
    {
        if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) target[state] = value;
    }

    private static void AddManifestState(Dictionary<PetState, string> states, PetState state, string key, string directory, JsonElement manifestStates)
    {
        if (!manifestStates.TryGetProperty(key, out var property) || property.GetString() is not { } relative) return;
        var path = ResolvePackagePath(directory, relative);
        ValidatePng(path);
        states[state] = path;
    }

    private static string ResolvePackagePath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("宠物包清单只能使用相对路径。");
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("宠物包清单包含越界路径。");
        return resolved;
    }

    private string GetPackageDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("自定义桌宠 ID 无效。");
        return Path.Combine(_packagesDirectory, id);
    }

    private static void AddOptionalState(Dictionary<PetState, string> states, PetState state, string path) { if (File.Exists(path)) states[state] = path; }
    private static void CopyPng(string source, string destination) { if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination, overwrite: true); }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record PngManifest(int SchemaVersion, string Id, string Name, string RendererType, PngManifestSettings RendererSettings);
    private sealed record PngManifestSettings(Dictionary<string, string> States);
    private sealed record Live2DManifest(int SchemaVersion, string Id, string Name, string RendererType, Live2DManifestSettings RendererSettings);
    private sealed record Live2DManifestSettings(
        string Model,
        string? Thumbnail,
        string Fit,
        double Scale,
        double OffsetX,
        double OffsetY,
        Dictionary<string, string> Motions,
        List<Live2DPointerTrackingSetting>? PointerTracking = null);
}
