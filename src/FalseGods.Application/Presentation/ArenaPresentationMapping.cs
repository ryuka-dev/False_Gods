using System;
using FalseGods.Core.Arena.Events;
using FalseGods.RuntimeContracts.Presentation;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// The host/single-player half of the <b>arena</b> presentation mapper: <see cref="IArenaDomainEvent"/>s
    /// from the live <c>ArenaSimulation</c> into presentation cues — the arena counterpart of
    /// <see cref="BossPresentationMapping"/> (Docs/Architecture.md §7).
    /// </summary>
    /// <remarks>
    /// Pure and static, and it throws on an unmapped event: silently dropping a new domain event would hide a
    /// missing visual, exactly as the boss mapper's contract does. The client half lives in
    /// <see cref="WirePresentationMapping"/>; parity between the two is asserted by tests so both modes drive
    /// identical cues.
    /// </remarks>
    public static class ArenaPresentationMapping
    {
        public static IPresentationEvent ToEvent(IArenaDomainEvent domainEvent)
        {
            switch (domainEvent)
            {
                case MechanismGroupActivated e:
                    return new MechanismGroupEngaged(e.Group);
                case ArenaExitUnlocked _:
                    return new ExitOpened();
                case null:
                    throw new ArgumentNullException(nameof(domainEvent));
                default:
                    throw new ArgumentOutOfRangeException(nameof(domainEvent), domainEvent.GetType().Name, "No presentation mapping for this arena domain event.");
            }
        }
    }
}
