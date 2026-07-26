using System;
using FalseGods.RuntimeContracts.Multiplayer;
using SULFURTogether.Api;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The ST implementation of <see cref="ISpawnOwnership"/>: declares one of our components a host-authoritative
    /// spawner on SULFUR Together's public spawn API, so the units it creates at runtime are mirrored onto every
    /// client as puppets the host drives.
    /// </summary>
    /// <remarks>
    /// <para>ST does not replicate unit creation in general — level enemies exist on every peer because every peer
    /// generates the level — so a unit spawned mid-fight is one-sided until its owner is declared. ST already
    /// mirrors such spawns for the sources it ships with; the public API is how a companion mod joins that list
    /// without ST learning anything about the mod.</para>
    /// <para>An ST build without that API is not an error here: the declaration fails, the caller is told by the
    /// <c>null</c> token, and the encounter runs with host-only minions rather than not running at all.</para>
    /// </remarks>
    internal sealed class StSpawnOwnership : ISpawnOwnership
    {
        private readonly ILogger? _logger;

        public StSpawnOwnership(ILogger? logger = null)
        {
            _logger = logger;
        }

        public IDisposable? DeclareHostAuthoritative(object spawnOwner)
        {
            if (!(spawnOwner is MonoBehaviour owner))
            {
                _logger?.LogWarning("[st-spawns] a spawn owner that is not a game component cannot be declared; "
                    + "runtime spawns will stay host-only.");
                return null;
            }

            try
            {
                var registration = NetExternalSpawns.RegisterHostAuthoritativeOwner(owner);
                _logger?.Log($"[st-spawns] '{owner.GetType().Name}' declared host-authoritative; its runtime "
                    + "spawns are mirrored to clients as host-driven puppets.");
                return registration;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[st-spawns] the session layer would not take the declaration "
                    + $"({exception.Message}); runtime spawns will stay host-only.");
                return null;
            }
        }
    }
}
