using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Combat
{
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
    /// <para><b>Host only</b>, like every other thing the boss puts in the world: arms a client raised for itself
    /// would double the barrage and aim it from places nobody else has an arm.</para>
    /// </remarks>
    public interface IBossArmPort
    {
        /// <summary>How many arms this port currently has standing.</summary>
        int Raised { get; }

        /// <summary>
        /// Put <paramref name="count"/> arms up around <paramref name="around"/>, flanking it at
        /// <paramref name="sideDistance"/> metres. Raising while arms are already up adds nothing: the rage puts
        /// them up once and they stay for its duration.
        /// </summary>
        /// <remarks>
        /// Only the place and the number are said here. Which way "to the side" points, and how a point is brought
        /// down onto ground an arm can stand in, are facts about the level and belong to whoever can ask it.
        /// </remarks>
        void Raise(ArenaWorldPoint around, int count, float sideDistance);

        /// <summary>
        /// Take every arm back down. The arms belong to the rage: a boss that has been supplied again, or a fight
        /// that ended, must not leave them throwing.
        /// </summary>
        void LowerAll();
    }
}
