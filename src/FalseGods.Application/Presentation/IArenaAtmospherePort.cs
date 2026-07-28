using System;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// What the room itself does when the fight starts: how far a player can see in it, and what is playing.
    /// </summary>
    /// <remarks>
    /// <para><b>Presentation, and made by every peer for itself.</b> Nothing here decides anything — the fog and
    /// the music are what a peer plays because the host's boss began, exactly as the roar is
    /// (<see cref="IBossVoicePort"/>). Two peers running this from the same replicated fact arrive at the same
    /// room without either telling the other about fog or music.</para>
    /// <para><b>The game's own settings apply to all of it.</b> The fog is the level's, changed through the
    /// engine's own request; the music is one of the game's own playlists started the way the game starts it, so
    /// the player's music volume, mute and mixer snapshot all hold. A mod does not get audio settings of its own
    /// (Docs/BossEncounterRunbook.md §3.14).</para>
    /// </remarks>
    public interface IArenaAtmospherePort : IDisposable
    {
        /// <summary>Fetch whatever this needs from the game ahead of the moment it is wanted. Safe to call again;
        /// what it could not get, it will try for again.</summary>
        void Warm();

        /// <summary>
        /// How deep the room reads: fog begins at <paramref name="startDistance"/> and swallows everything past
        /// <paramref name="endDistance"/>.
        /// </summary>
        /// <param name="afterSeconds">Wait this long before starting. The pause before a room opens is the point of
        /// it — the players are given a beat to be standing in the dark before it lifts.</param>
        /// <param name="overSeconds">How long the change itself takes. Zero snaps, which is what a peer catching up
        /// on an opening it missed should do.</param>
        void SetRoomDepth(float startDistance, float endDistance, float afterSeconds = 0f, float overSeconds = 0f);

        /// <summary>Start the boss battle music.</summary>
        void StartBattleMusic();

        /// <summary>Stop the boss battle music, the way the game ends a boss fight's.</summary>
        void StopBattleMusic();
    }

    /// <summary>
    /// How deep the boss arena reads, before the fight and during it, and how it gets from one to the other.
    /// </summary>
    /// <remarks>
    /// <para>Shared because three things need the same numbers and must not each keep their own: the level load
    /// (which builds the room already dark), the host's encounter (which opens it), and a client's (which opens
    /// its own copy off the same replicated fact). Constants for the same reason the arms' placement is —
    /// found by eye in the room and then settled, destined for authored boss content rather than a player-facing
    /// setting.</para>
    /// <para><b>Not only a look.</b> The game clamps its aim assist to the fog cutoff, so how far the room can be
    /// seen is also how far a player's aim is helped.</para>
    /// </remarks>
    public static class ArenaDepth
    {
        /// <summary>Before the fight: the players walk in with a torch's worth of room around them and a wall of
        /// black past it, so what they can hear is all they have.</summary>
        public const float OpeningStart = 4f;

        public const float OpeningEnd = 10f;

        /// <summary>During the fight: the far side of the room goes before the wall does, which is what makes an
        /// eighty-metre arena read as a cave rather than as a box.</summary>
        public const float FightStart = 10f;

        public const float FightEnd = 72f;

        /// <summary>How long the players are left in the dark with the boss already roaring, before the room opens.
        /// The pause is the point: without it the reveal is something that merely happened.</summary>
        public const float RevealHoldSeconds = 1f;

        /// <summary>How long the room then takes to open out.</summary>
        public const float RevealSeconds = 3f;
    }
}
