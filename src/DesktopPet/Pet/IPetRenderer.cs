using System.Windows;
using System.Threading.Tasks;

namespace DesktopPet.Pet;

public interface IPetRenderer : IDisposable
{
    FrameworkElement View { get; }
    Task Ready { get; }
    event Action<string>? Failed;
    void Show(PetState state);
}
