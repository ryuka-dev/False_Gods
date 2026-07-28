using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Presentation
{
    /// <summary>
    /// The noise the boss makes — currently the one it makes when being starved stops being something it will
    /// wait out.
    /// </summary>
    /// <remarks>
    /// <para><b>Every peer makes its own.</b> A roar is presentation: it decides nothing, and the fact that
    /// provokes it already reaches every peer, both as the reliable change and as the state a peer corrects
    /// itself from. So each end plays it where its own player is standing and hears it at the distance it should
    /// be heard from, rather than the host playing a sound only the host can hear.</para>
    /// <para><b>The sound is the game's.</b> The cave boss already has a roar, and it is the one players have
    /// heard it make; taking that rather than inventing one is the same choice as taking its arms and its mud.
    /// </para>
    /// </remarks>
    public interface IBossVoicePort
    {
        /// <summary>
        /// Have the boss roar at <paramref name="at"/>.
        /// </summary>
        /// <remarks>
        /// Silence is an acceptable outcome: a build that cannot find the sound still gets the fight, and a roar
        /// that has to be fetched before it can be made may be missed the first time rather than held for.
        /// </remarks>
        void Roar(ArenaWorldPoint at);

        /// <summary>
        /// Fetch whatever the voice needs, so the first roar is not the thing that waits for it. Safe to repeat
        /// and safe to skip — <see cref="Roar"/> asks for anything still missing itself.
        /// </summary>
        void Warm();
    }
}
