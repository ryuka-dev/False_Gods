namespace FalseGods.Core.Arena.Events
{
    /// <summary>A mechanism group switched on — the first time that group is activated in the encounter.</summary>
    public sealed record MechanismGroupActivated(MechanismGroupId Group) : IArenaDomainEvent;

    /// <summary>The arena exit unlocked — the boss is defeated and players may leave.</summary>
    public sealed record ArenaExitUnlocked() : IArenaDomainEvent;
}
