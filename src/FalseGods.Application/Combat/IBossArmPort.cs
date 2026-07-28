using FalseGods.Core.Simulation;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Where a boss's arms stand: at its sides, in its own facing frame, so they turn and travel with it.
    /// </summary>
    /// <remarks>
    /// <para><b>Read off the boss, not off the room.</b> An arm placed by measuring the level lands wherever the
    /// level happens to be — under a prop, on the floor beneath a pool, in a rock. The boss already stands
    /// somewhere the room authored for it, at a height the room authored for it, so taking the arm's place from
    /// the boss inherits both and needs to measure nothing.</para>
    /// <para>The offsets and the size are the <i>boss's</i>, not the adapter's, and are carried per call rather
    /// than settled once: the same port then serves whatever a second boss asks for, and a change reaches arms
    /// that are already standing rather than waiting for the next ones.</para>
    /// </remarks>
    /// <param name="BossAt">Where the boss is standing, in world space.</param>
    /// <param name="BossFacing">Which way it is facing on the ground plane; a zero vector means "not facing
    /// anywhere", and the arms keep whatever axis they can still work out.</param>
    /// <param name="SideDistance">How far to either side of the boss the arms stand.</param>
    /// <param name="ForwardOffset">How far in front of it (negative is behind).</param>
    /// <param name="Lift">How far above the boss's own footing, so an arm can be sunk into the ground or raised
    /// out of it without moving the boss.</param>
    /// <param name="Scale">How large the arm is drawn, against the size the game authored it at. A boss shown
    /// larger than the one these arms were drawn for needs them enlarged to match, or they read as a different
    /// creature's; a non-positive value leaves whatever size the game gave them.</param>
    public readonly record struct ArmPlacement(
        ArenaWorldPoint BossAt,
        SimVector2 BossFacing,
        float SideDistance,
        float ForwardOffset,
        float Lift,
        float Scale);

    /// <summary>
    /// The arms a starved boss puts up beside itself, which lob mud at everyone while it has nothing to throw.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an arm and not another minion.</b> The rage already asks the players for two jobs — kill the
    /// band, and get the route running again — and both are answered by walking somewhere and shooting something.
    /// An arm asks for neither: it cannot be killed and it cannot be reached, it simply keeps throwing until the
    /// supply comes back. That is what makes starving the boss a decision with a running cost rather than a state
    /// the players clear and then ignore.</para>
    /// <para><b>It is the game's own arm.</b> The cave boss already grows these out of the sludge, and the whole
    /// appear-throw-sink cycle lives in that creature's own behaviour tree and animator — nothing here drives it.
    /// So an arm gets its aiming, its arc, its mud ball, its sound and its damage from the game, and the session
    /// layer mirrors it to clients the way it mirrors every other host-spawned unit.</para>
    /// <para><b>They belong to the boss, not to a spot on the floor.</b> The creature has no navigation agent and
    /// cannot take a step, so an arm left where it first went up would be abandoned the moment the boss moved on.
    /// Driving them from the boss every frame is what keeps them its arms.</para>
    /// <para><b>Raising is host only</b>, like every other thing the boss puts in the world: arms a client raised
    /// for itself would double the barrage. <b>Carrying them is every peer's own job</b>, because the session layer
    /// mirrors the raising and not the following — see <see cref="Adopt"/>.</para>
    /// </remarks>
    public interface IBossArmPort
    {
        /// <summary>How many arms this port currently has standing.</summary>
        int Raised { get; }

        /// <summary>
        /// Put <paramref name="count"/> arms up around the boss. Raising while arms are already up adds nothing:
        /// the rage puts them up once and they stay for its duration.
        /// </summary>
        void Raise(int count, ArmPlacement placement);

        /// <summary>
        /// Carry the standing arms with the boss for one frame. Does nothing when none are up, so it is safe to
        /// call every frame of a fight rather than only while the boss is enraged.
        /// </summary>
        void Follow(ArmPlacement placement);

        /// <summary>
        /// Take every arm back down. The arms belong to the rage: a boss that has been supplied again, or a fight
        /// that ended, must not leave them throwing.
        /// </summary>
        void LowerAll();

        /// <summary>
        /// Take charge of arms that are already standing in the world without having raised them, so they can be
        /// carried with the boss. Repeating it is free once they are held.
        /// </summary>
        /// <remarks>
        /// This is what a peer that is not the host does. The session layer puts the host's arms into a client's
        /// world when they are raised but does not go on telling it where they are afterwards, so a client's copies
        /// stand wherever they first appeared while the host's follow the boss across the room. The client has
        /// everything it needs to place them itself — the boss's replicated pose, and whether it is enraged — so it
        /// does, and the two ends agree again.
        /// </remarks>
        void Adopt(int count);

        /// <summary>
        /// Let go of the arms without taking them down: they are somebody else's to end.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="Adopt"/>. A client must not kill the host's arms — the session layer
        /// already mirrors their deaths — it only has to stop carrying them.
        /// </remarks>
        void Release();
    }

    /// <summary>
    /// Where a starved boss's arms stand and how large they are drawn, shared so that every peer places them the
    /// same way.
    /// </summary>
    /// <remarks>
    /// <b>Found by eye, in the room.</b> There is no reasoning that produces these; they were tuned live against
    /// the boss until the arms read as its own. Just far enough apart to clear its body, a pace in front so they
    /// are between it and the fight, lifted out of the ground to where the creature's own footing sits, and at the
    /// same enlargement the boss body is shown at — the arms are drawn for a boss the size of the vanilla one, and
    /// ours is one and a half times that (Docs/BossEncounterRunbook.md §2.5).
    /// <para>Boss design, and code rather than content for the same reason as the boss's itinerary: there is no
    /// boss-content pipeline yet and one boss does not justify inventing one (Docs/DefinitionOfDone.md §3).</para>
    /// </remarks>
    public static class RageArms
    {
        public const int Count = 2;

        public const float SideDistance = 3.1f;

        public const float ForwardOffset = 1f;

        public const float Lift = 1.5f;

        public const float Scale = 1.5f;
    }
}
