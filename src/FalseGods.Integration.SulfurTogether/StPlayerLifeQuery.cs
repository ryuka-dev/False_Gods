using System;
using FalseGods.RuntimeContracts.Multiplayer;
using SULFURTogether.Api;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The ST implementation of <see cref="IPlayerLifeQuery"/>: asks SULFUR Together whether a player is down.
    /// </summary>
    /// <remarks>
    /// <para><b>Most of the answer is already free.</b> ST drops a downed <i>remote</i> player from the game's own
    /// player roster, so anything reading that roster — which is how the boss finds its targets — stops seeing
    /// them without asking. What remains is the local player, who stays in that roster while downed because they
    /// are the game's own player singleton, and that is what this asks about.</para>
    /// <para>An ST build without the API is not an error: the question goes unanswered, every player counts as
    /// fighting, and the encounter behaves as it did before. Asked once per player per frame, so the failure is
    /// remembered rather than thrown repeatedly.</para>
    /// </remarks>
    internal sealed class StPlayerLifeQuery : IPlayerLifeQuery
    {
        private readonly ILogger? _logger;
        private bool _unavailable;

        public StPlayerLifeQuery(ILogger? logger = null)
        {
            _logger = logger;
        }

        public bool IsOutOfTheFight(object playerUnit)
        {
            if (playerUnit is null || _unavailable)
            {
                return false;
            }

            try
            {
                return NetPlayerLife.IsOutOfTheFight(playerUnit);
            }
            catch (Exception exception)
            {
                // Most likely an ST without this API. Stop asking rather than throwing once a frame per player.
                _unavailable = true;
                _logger?.LogWarning($"[st-life] the session layer cannot say who is down ({exception.Message}); "
                    + "the boss will treat every player as still fighting.");
                return false;
            }
        }
    }
}
