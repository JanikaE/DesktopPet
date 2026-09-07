using System.Windows.Media.Imaging;

namespace DesktopPet.Pet;

/// <summary>Maps each pet state to a single static PNG; no frame timer is involved.</summary>
public sealed class PetImageStatePresenter
{
    private readonly IReadOnlyDictionary<PetState, BitmapImage> _images;

    public PetImageStatePresenter(string assetsDirectory)
    {
        var idle = LoadImage(Path.Combine(assetsDirectory, "idle.png"));
        _images = new Dictionary<PetState, BitmapImage>
        {
            [PetState.Idle] = idle,
            [PetState.Click] = LoadImageOrFallback(Path.Combine(assetsDirectory, "click.png"), idle),
            [PetState.Dragging] = LoadImageOrFallback(Path.Combine(assetsDirectory, "drag.png"), idle)
        };
    }

    public event EventHandler<BitmapImage>? ImageChanged;

    public void Show(PetState state)
    {
        if (_images.TryGetValue(state, out var image)) ImageChanged?.Invoke(this, image);
    }

    private static BitmapImage LoadImageOrFallback(string path, BitmapImage fallback) =>
        File.Exists(path) ? LoadImage(path) : fallback;

    private static BitmapImage LoadImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required idle pet image was not found.", path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
