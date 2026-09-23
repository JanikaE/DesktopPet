namespace DesktopPet.Pet;

public sealed record PetDefinition(
    string Id,
    string Name,
    string RendererType,
    bool IsBuiltIn,
    string PackageDirectory,
    IReadOnlyDictionary<PetState, string> StateImages)
{
    public string IdleImagePath => StateImages[PetState.Idle];
}
