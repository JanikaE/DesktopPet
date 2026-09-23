using System.Windows;

namespace DesktopPet.Pet;

public interface IPetRenderer : IDisposable
{
    FrameworkElement View { get; }
    void Show(PetState state);
}
