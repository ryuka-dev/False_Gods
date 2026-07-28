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
    /// <para>The three offsets are boss design and are expected to be tuned; they are carried per call rather
    /// than fixed in the adapter so the same port serves whatever a boss asks for.</para>
    /// </remarks>
    /// <param name="BossAt">Where the boss is standing, in world space.</param>
    /// <param name="BossFacing">Which way it is facing on the ground plane; a zero vector means "not facing
    /// anywhere", and the arms keep whatever axis they can still work out.</param>
    /// <param name="SideDistance">How far to either side of the boss the arms stand.</param>
    /// <param name="ForwardOffset">How far in front of it (negative is behind).</param>
    /// <param name="Lift">How far above the boss's own footing, so an arm can be sunk into the ground or raised
    /// out of it without moving the boss.</param>
    public readonly record struct ArmPlacement(
        ArenaWorldPoint BossAt,
        SimVector2 BossFacing,
        float SideDistance,
        float ForwardOffset,
        float Lift);

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
    /// <para><b>Host only</b>, like every other thing the boss puts in the world: arms a client raised for itself
    /// would double the barrage and aim it from places nobody else has an arm.</para>
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
    }
}
