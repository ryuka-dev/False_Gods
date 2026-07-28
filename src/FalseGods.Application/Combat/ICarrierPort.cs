using System.Collections.Generic;
using FalseGods.Core.Bosses.Combat;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// The goblins who carry the boss's ammunition from the room's production points to the pile beside it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the supply is a walk and not a number.</b> The boss's barrage is limited by how much reaches
    /// it, and what reaches it is carried across the room on foot. That makes the route a thing a player can stand
    /// in: kill a carrier and the boss goes hungry for as long as the walk takes to replace. A supply rate that
    /// was simply configured would make the goblins scenery.</para>
    /// <para><b>The game's own units, driven the game's own way.</b> A carrier is a real goblin, told where to go
    /// through the same forced-destination path the game uses for its own scripted walks — which switches the
    /// creature's behaviour tree off for the trip. That is also why a civilian does not panic and flee while
    /// carrying: panic lives in the behaviour tree, so a carrier on an errand has none.</para>
    /// <para><b>Host only</b>, like the summoned minions: the host owns the world, and carriers a client spawned
    /// itself would double the supply and diverge the fight.</para>
    /// </remarks>
    public interface ICarrierPort
    {
        /// <summary>How many carriers are alive and working the route.</summary>
        int Working { get; }

        /// <summary>How many destructibles are on carriers' backs right now — supply in transit, neither at a
        /// production point nor available to the boss. Diagnostic.</summary>
        int Carried { get; }

        /// <summary>
        /// How fast the carriers actually walk, in metres per second, or 0 before any has been seen.
        /// </summary>
        /// <remarks>
        /// Reported rather than assumed because it is the divisor that turns carriers and loads into crates per
        /// second: it belongs to the creature and the game's own tuning, so guessing it means every rate derived
        /// from it is wrong by the same factor. Measured off the first carrier that exists.
        /// </remarks>
        float ObservedWalkSpeed { get; }

        /// <summary>
        /// Run the supply route for one frame. <paramref name="wanted"/> carriers should be on it, each hauling
        /// <paramref name="loadPerCarrier"/>; carriers are put on and taken off the route as those change.
        /// <paramref name="sources"/> are the room's production points, and <paramref name="deliverTo"/> /
        /// <paramref name="deliverPile"/> are where the pile beside the boss is and what it is called — both of
        /// which move when the boss does.
        /// </summary>
        /// <param name="replaceAfterSeconds">How long the route stays a carrier short after one is killed. This is
        /// what makes killing them worth doing: the headcount is otherwise restored the same frame it drops, and a
        /// player who fights their way to the route has bought the boss nothing. It applies only to a carrier that
        /// <i>died</i> — filling the route at the start of a fight, and reinforcing it when the village steps up,
        /// are not punishments and are not delayed.</param>
        void Advance(
            float deltaSeconds,
            int wanted,
            int loadPerCarrier,
            float replaceAfterSeconds,
            IReadOnlyList<ArenaWorldPoint> sources,
            ArenaWorldPoint deliverTo,
            CratePileId deliverPile);

        /// <summary>Take every carrier off the route and out of the world. The supply line belongs to the fight:
        /// one that ended must not leave goblins walking crates to a boss that is gone.</summary>
        void DismissAll();
    }
}
