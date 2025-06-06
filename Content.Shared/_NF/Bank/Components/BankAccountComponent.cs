using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Bank.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class BankAccountComponent : Component
{
    [DataField("balance")]
    public long Balance;
}
[Serializable, NetSerializable]
public sealed partial class BankAccountComponentState : ComponentState
{
    public long Balance;
}
