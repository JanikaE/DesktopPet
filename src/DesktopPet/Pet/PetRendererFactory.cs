namespace DesktopPet.Pet;

public static class PetRendererFactory
{
    public static IPetRenderer Create(PetDefinition definition) => definition.RendererType switch
    {
        "png" => new PngPetRenderer(definition),
        _ => throw new NotSupportedException($"不支持的桌宠渲染类型：{definition.RendererType}")
    };
}
