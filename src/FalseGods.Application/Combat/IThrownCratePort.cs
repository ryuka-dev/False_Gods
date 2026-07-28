using System;
using System.Collections.Generic;
using FalseGods.Core.Bosses.Combat;
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

        /// <summary>
        /// Drop one crate at <paramref name="at"/> under real gravity and let the game's own physics own it: it
        /// falls, comes to rest, and stacks with others already on the ground. This is the resting half of a
        /// destructible's life — produced, piled, carried, set back down — as opposed to <see cref="Throw"/>'s
        /// simulation-driven flight; nothing here drives the crate's position. It stays shootable while it rests.
        /// <paramref name="pile"/> is what the crate becomes: produced at a source, delivered to the boss, or
        /// merely lying there. Returns false when the crate could not be created.
        /// </summary>
        bool Drop(ArenaWorldPoint at, CratePileId pile);

        /// <summary>
        /// Lift up to <see cref="CrateVolleyShape.Count"/> crates off <paramref name="pile"/> — floating them up
        /// under our control rather than gravity — hold them a beat, then throw them as a shotgun spread.
        /// <para>Each crate picks one of <paramref name="aims"/> — one per player worth throwing at — and then
        /// either where that player is now or where they are predicted to be, split by
        /// <see cref="CrateVolleyShape.LeadShare"/>, before being scattered around that point. Every one of those
        /// choices comes from the shape's seed, so each peer lays the same volley out identically. Aiming per
        /// crate rather than per volley is what stops one player being safe while another is buried.</para>
        /// <para>Only crates resting <b>on that pile</b> are lifted, which is what makes the supply line a
        /// mechanic: a boss cannot fire the crates still standing at the production points, only what was carried
        /// to it. Returns how many were launched, which is zero when the pile is empty — an unsupplied boss — or
        /// when there is nobody left to throw at.</para>
        /// </summary>
        int LaunchVolley(CratePileId pile, IReadOnlyList<CrateVolleyAim> aims, CrateVolleyShape shape);

        /// <summary>
        /// Toss <paramref name="count"/> crates from <paramref name="from"/> onto the ground in a ring around
        /// <paramref name="at"/>, where they come to rest on <paramref name="pile"/> under the game's own physics.
        /// The ring is laid out from <paramref name="seed"/>. Returns how many were made.
        /// </summary>
        /// <remarks>
        /// <para>The gentle end of a destructible's life, and the opposite of <see cref="Throw"/>: nothing
        /// splashes, nothing breaks, and the crates are simply on the ground afterwards. This is a carrier setting
        /// a load down, or spilling one — the arc out of its hands is what makes a delivery read as an act rather
        /// than crates appearing beside a goblin.</para>
        /// <para><b>A ring, not a column</b>, because crates are solid bodies with real mass: a load released down
        /// one line spawns them inside each other and physics answers by firing them across the room.</para>
        /// <para><b>Seeded, and here rather than with the carriers</b>, because every peer has to lay the same
        /// load out the same way from the same few numbers — a client has no carriers of its own to ask.</para>
        /// </remarks>
        int TossRing(ArenaWorldPoint from, ArenaWorldPoint at, CratePileId pile, int count, int seed);

        /// <summary>How many crates are resting on one pile. This is what the supply line is told to decide
        /// whether a production point is full and whether a delivery pile has room.</summary>
        int RestingOn(CratePileId pile);

        /// <summary>
        /// Take up to <paramref name="count"/> resting crates off <paramref name="pile"/> and remove them from the
        /// world without dropping loot — a carrier picking a load up off a production point. Returns how many were
        /// actually taken, which is fewer than asked when the pile is short.
        /// </summary>
        /// <remarks>
        /// No loot, because the crate has not been destroyed: it is being moved, and it comes back at the other
        /// end. A carried load is deliberately <b>not</b> kept as live destructibles — a dozen carriers each
        /// holding a dozen real breakables would be hundreds of bodies walking around for something the player
        /// cannot interact with anyway — so the crates stop existing here and are made again where they are set
        /// down.
        /// </remarks>
        /// <param name="near">Where the taker is standing.</param>
        /// <param name="radius">How far it can reach. Crates further than this are left alone, so a carrier
        /// standing on one heap does not quietly collect another one across the room.</param>
        int TakeFrom(CratePileId pile, int count, ArenaWorldPoint near, float radius);

        /// <summary>
        /// Where the nearest crate resting on <paramref name="pile"/> lies, measured on the ground plane from
        /// <paramref name="near"/>. False when that pile is empty.
        /// </summary>
        /// <remarks>
        /// What a carrier uses to notice the crates a dead colleague spilled: given the choice between walking to
        /// a production point and picking up a load already lying closer, it takes the closer one. That is what
        /// keeps a long fight from silting up with abandoned cargo without anything having to vanish.
        /// </remarks>
        bool TryFindNearestResting(CratePileId pile, ArenaWorldPoint near, out ArenaWorldPoint at);

        /// <summary>
        /// A destructible died in a way the other peers cannot work out for themselves: a player broke it, or it
        /// burst on one. Everything else — a landing, a wall, a lift into a volley — follows the same arc from the
        /// same seed everywhere and is already agreed on without anybody saying anything.
        /// </summary>
        /// <remarks>Answered by whoever is composing this port, because what to do with it depends on who this
        /// peer is: a host says so to everyone, a client asks the host to settle it. Never raised while carrying
        /// out a destruction that arrived from somewhere else.</remarks>
        Action<int, CrateDeath>? Died { get; set; }

        /// <summary>
        /// Carry out a destruction decided elsewhere: destroy the destructible numbered <paramref name="crateId"/>
        /// the way <paramref name="death"/> says. Returns whether anything was destroyed.
        /// </summary>
        /// <remarks>
        /// A number this peer does not have is nothing to do. The peers count the same crates from the same
        /// commands, but piles settle under physics rather than under the commands alone, so they can disagree
        /// about which of a heap a carrier picked up; destroying nothing is the right answer to that, and the
        /// false return is how often it happens — worth watching rather than assuming.
        /// </remarks>
        bool Destroy(int crateId, CrateDeath death);

        /// <summary>Move every crate still in the air, and resolve the ones that have arrived or been broken.</summary>
        void Advance(float deltaSeconds);

        /// <summary>How many crates are in the air right now. Diagnostic.</summary>
        int InFlight { get; }

        /// <summary>How many crates are resting on the ground right now. Diagnostic.</summary>
        int Resting { get; }

        /// <summary>Drop everything: crates still in the air are removed without dropping loot, and any held
        /// content is released. Idempotent.</summary>
        void Release();
    }
}
