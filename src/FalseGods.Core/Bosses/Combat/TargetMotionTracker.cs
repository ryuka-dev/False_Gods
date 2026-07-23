using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// Smooths a target's velocity over a short window, so a lead aimed with it follows where the target is really
    /// going rather than which way it flinched at the instant of firing.
    /// </summary>
    /// <remarks>
    /// <para><b>Why smooth at all.</b> A player standing still and tapping left–right produces, at any single
    /// instant, a full-speed velocity pointing fully to one side — enough to throw a lead aimed over a couple of
    /// seconds far past them. Their <i>average</i> velocity over half a second, though, is almost nothing, because
    /// they are not actually going anywhere. Leading with the average is what tells a genuine sprint apart from a
    /// jittery bluff: the sprint survives the averaging, the bluff cancels itself out.</para>
    /// <para><b>Why an exponential average.</b> One remembered vector, nudged toward each new sample by an amount
    /// that scales with elapsed time, gives a frame-rate-independent smoothing with no history buffer to size or
    /// keep. A larger <see cref="_smoothingSeconds"/> damps harder — more bluff-proof, but slower to trust a real
    /// change of direction.</para>
    /// </remarks>
    public sealed class TargetMotionTracker
    {
        private readonly float _smoothingSeconds;
        private SimVector2 _smoothed = SimVector2.Zero;
        private bool _hasSample;

        /// <param name="smoothingSeconds">Roughly the window the velocity is averaged over; larger damps harder.
        /// Non-positive is treated as no smoothing (each sample is taken as-is).</param>
        public TargetMotionTracker(float smoothingSeconds)
        {
            _smoothingSeconds = smoothingSeconds;
        }

        /// <summary>The smoothed velocity, or zero before any sample has been observed.</summary>
        public SimVector2 SmoothedVelocity => _hasSample ? _smoothed : SimVector2.Zero;

        /// <summary>Fold one instantaneous <paramref name="velocity"/> reading, taken
        /// <paramref name="deltaSeconds"/> after the last, into the running average.</summary>
        public void Observe(SimVector2 velocity, float deltaSeconds)
        {
            if (!_hasSample || _smoothingSeconds <= 0f || deltaSeconds <= 0f)
            {
                // First reading (nothing to blend with) or smoothing switched off: take the sample as it is.
                _smoothed = velocity;
                _hasSample = true;
                return;
            }

            // Fraction of the way from the remembered average to the new sample, scaled by how much time passed —
            // so the smoothing is the same whether the game ticks fast or slow. Clamped so a long frame cannot
            // overshoot past the sample.
            var blend = deltaSeconds / _smoothingSeconds;
            if (blend > 1f)
            {
                blend = 1f;
            }

            _smoothed = new SimVector2(
                _smoothed.X + (velocity.X - _smoothed.X) * blend,
                _smoothed.Z + (velocity.Z - _smoothed.Z) * blend);
        }

        /// <summary>Forget the history — no target to track (no level, or the player is gone) — so the next sample
        /// starts the average fresh instead of blending against a stale one.</summary>
        public void Reset()
        {
            _smoothed = SimVector2.Zero;
            _hasSample = false;
        }
    }
}
