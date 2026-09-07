namespace DesktopPet.Pet;

public sealed class PetStateMachine
{
    public PetState Current { get; private set; } = PetState.Idle;
    public event EventHandler<PetState>? StateChanged;

    public void BeginDrag() => TransitionTo(PetState.Dragging);
    public void EndDrag() => TransitionTo(PetState.Idle);
    public void PlayClick() => TransitionTo(PetState.Click);

    public void FinishAnimation() => TransitionTo(PetState.Idle);

    private void TransitionTo(PetState next)
    {
        if (Current == next) return;
        Current = next;
        StateChanged?.Invoke(this, next);
    }
}
