using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Throws the game's own destructibles — barrels and crates — along a simulation-decided flight, and owns
    /// them until they land or are broken.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the game's destructibles rather than our own.</b> A vanilla breakable is a <i>unit</i>: it
    /// already has health, already takes ordinary weapon fire through the game's own hit path, already drops loot
    /// when it breaks, and — because it is the same object the game drops in its own levels — already obeys
    /// whatever rules a multiplayer session has for sharing that loot. Reimplementing the crate would mean
    /// reimplementing all four, and would leave our crates behaving differently from every other barrel in the
    /// game.</para>
    /// <para><b>Two ways to die, and only one of them pays.</b> Broken by a player, a crate goes through the
    /// game's own break — which is what drops the loot. Landing, it breaks without dropping. That asymmetry is
    /// the point: the loot rewards shooting a crate out of the air, and a boss with an endless supply of crates
    /// cannot be farmed by standing still and letting them land.</para>
    /// <para><b>Flight is not physics.</b> The path comes from the simulation, so every peer computes the same
    /// one from the same few numbers instead of the host streaming positions (see
    /// <c>FalseGods.Core.Bosses.Combat.BallisticArc</c>).</para>
    /// </remarks>
    public interface IThrownCratePort
    {
        /// <summary>
        /// Get the crate content ready so that throwing one later costs nothing. Returns false when the content
        /// is unavailable, in which case throwing will not work and the caller should say so rather than fail
        /// mid-fight.
        /// </summary>
        bool Prepare();

        /// <summary>
        /// Throw one crate from <paramref name="from"/> so that it lands on <paramref name="to"/> after
        /// <paramref name="flightSeconds"/>, arcing <paramref name="apexHeight"/> above the straight line
        /// between them. Returns false when the crate could not be created.
        /// </summary>
        bool Throw(ArenaWorldPoint from, ArenaWorldPoint to, float flightSeconds, float apexHeight);

        /// <summary>Move every crate still in the air, and resolve the ones that have arrived or been broken.</summary>
        void Advance(float deltaSeconds);

        /// <summary>How many crates are in the air right now. Diagnostic.</summary>
        int InFlight { get; }

        /// <summary>Drop everything: crates still in the air are removed without dropping loot, and any held
        /// content is released. Idempotent.</summary>
        void Release();
    }
}
