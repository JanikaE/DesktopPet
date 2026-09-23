using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopPet.Pet;

public sealed class PngPetRenderer : IPetRenderer
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly IReadOnlyDictionary<PetState, BitmapImage> _images;

    public PngPetRenderer(PetDefinition definition)
    {
        var idle = LoadImage(definition.IdleImagePath);
        var images = new Dictionary<PetState, BitmapImage> { [PetState.Idle] = idle };
        images[PetState.Click] = definition.StateImages.TryGetValue(PetState.Click, out var click) ? LoadImage(click) : idle;
        images[PetState.Dragging] = definition.StateImages.TryGetValue(PetState.Dragging, out var dragging) ? LoadImage(dragging) : idle;
        _images = images;
        _image.Source = idle;
    }

    public System.Windows.FrameworkElement View => _image;

    public void Show(PetState state)
    {
        if (_images.TryGetValue(state, out var image)) _image.Source = image;
    }

    public void Dispose() => _image.Source = null;

    private static BitmapImage LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
