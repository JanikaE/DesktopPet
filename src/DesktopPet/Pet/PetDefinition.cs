namespace DesktopPet.Pet;

public sealed record PetDefinition(
    string Id,
    string Name,
    string RendererType,
    bool IsBuiltIn,
    string PackageDirectory,
    IReadOnlyDictionary<PetState, string> StateImages,
    Live2DRendererSettings? Live2D = null)
{
    public string? IdleImagePath => StateImages.GetValueOrDefault(PetState.Idle);
    public bool IsLive2D => string.Equals(RendererType, "live2d-cubism", StringComparison.Ordinal);
    public string? PreviewImagePath => IsLive2D
        ? Live2D?.ThumbnailPath
        : StateImages.GetValueOrDefault(PetState.Idle);
}

public sealed record Live2DRendererSettings(
    string ModelPath,
    string? ThumbnailPath,
    string Fit,
    double Scale,
    double OffsetX,
    double OffsetY,
    IReadOnlyDictionary<PetState, string> Motions,
    IReadOnlyList<Live2DPointerTrackingSetting> PointerTracking);

public sealed record Live2DPointerTrackingSetting(
    string ParameterId,
    double Impact,
    bool Reflect,
    string Source);

public sealed record Live2DPointerParameterDefinition(string Name, string ParameterId);

public static class Live2DPointerTracking
{
    public const string MouseLeftX = "mouseLeftX";
    public const string MouseLeftY = "mouseLeftY";

    public static IReadOnlyList<Live2DPointerParameterDefinition> Parameters { get; } =
    [
        new("角度 X", "ParamAngleX"),
        new("角度 Y", "ParamAngleY"),
        new("角度 Z", "ParamAngleZ"),
        new("身体角度 X", "ParamBodyAngleX"),
        new("身体角度 Y", "ParamBodyAngleY"),
        new("身体角度 Z", "ParamBodyAngleZ"),
        new("左眼开合", "ParamEyeLOpen"),
        new("右眼开合", "ParamEyeROpen"),
        new("眼球 X", "ParamEyeBallX"),
        new("眼球 Y", "ParamEyeBallY"),
        new("果冻眼", "ParamEyeBallForm"),
        new("左眉 Y", "ParamBrowLY"),
        new("右眉 Y", "ParamBrowRY"),
        new("嘴型", "ParamMouthForm"),
        new("嘴巴开合", "ParamMouthOpenY"),
        new("呼吸", "ParamBreath"),
        new("前发摆动", "ParamHairFront"),
        new("后发摆动", "ParamHairBack")
    ];

    public static IReadOnlyList<Live2DPointerTrackingSetting> CreateDefaults() =>
    [
        new("ParamAngleX", 100, false, MouseLeftX),
        new("ParamAngleY", 100, false, MouseLeftY),
        new("ParamBodyAngleX", 100, false, MouseLeftX),
        new("ParamEyeBallX", 100, false, MouseLeftX),
        new("ParamEyeBallY", 100, false, MouseLeftY)
    ];
}

public sealed record Live2DModelInspection(
    string ModelPath,
    string SourceDirectory,
    IReadOnlyList<string> ResourcePaths,
    IReadOnlyList<string> MotionGroups,
    long TotalBytes);
