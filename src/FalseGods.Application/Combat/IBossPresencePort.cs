namespace FalseGods.Application.Combat
{
    /// <summary>
    /// Makes the boss something the game's own weapon systems can see.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it needs saying at all.</b> The boss is ours: its state, its movement and its damage are
    /// decided by our own simulation, and it is drawn by our own renderer. That is right, and it is also why the
    /// game does not know it exists — so a player firing a homing weapon at it watches the shots ignore it, and
    /// the game's aim assist pulls their aim off it towards nothing. Both of those read as the boss being fake,
    /// which is the one thing all the rest of this work is trying not to be.</para>
    /// <para><b>Presence, not identity.</b> This makes the boss <i>visible to the systems that look for enemies</i>
    /// and nothing more. It does not hand the game the boss's health, its death, its loot, its behaviour or its
    /// movement — those stay where they are. The distinction matters because the alternative, making the boss a
    /// real creature of the game's, would put two authorities on the same thing.</para>
    /// <para><b>Every peer for itself.</b> Aim assist and weapon homing run on the machine doing the aiming, so
    /// each peer declares its own boss to its own game. Nothing about this is replicated and nothing about it is
    /// a decision.</para>
    /// </remarks>
    public interface IBossPresencePort
    {
        /// <summary>
        /// Declare the boss to the game as something worth aiming at. Repeating it is free; where the boss is and
        /// how big it is are read from the boss itself.
        /// </summary>
        void Declare();

        /// <summary>Take the declaration back. A fight that ended must not leave the game aiming at a boss that
        /// is no longer there. Also takes the boss off the game's boss bar.</summary>
        void Withdraw();

        /// <summary>
        /// Put the boss on the game's own boss bar — the one across the top of the heads-up display. Repeating it
        /// is free.
        /// </summary>
        /// <remarks>
        /// The bar is the game's, so it looks and behaves like every other boss fight's: the same frame, the same
        /// colour, the same animation coming in. Shown when the fight begins rather than when the boss is placed —
        /// a creature standing in a room nobody has walked into is not a boss fight yet.
        /// </remarks>
        void ShowHealthBar();

        /// <summary>How full the bar is, as a fraction in [0, 1]. Pushed by whoever knows the boss's health: the
        /// host from its own simulation, a client from the host's replicated state, so both see one bar move
        /// together.</summary>
        void ReportHealth(float fraction);

        /// <summary>Take the boss off the bar, the way the game does when one of its own bosses dies.</summary>
        void HideHealthBar();
    }
}
