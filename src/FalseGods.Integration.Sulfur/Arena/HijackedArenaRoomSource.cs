// Unity / game-type interop, like the other game-facing implementations in this assembly.
#nullable disable

using System;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>What loading the arena content produced: the authored player-spawn position, or why it failed.</summary>
    public sealed class HijackedArenaLoad
    {
        private HijackedArenaLoad(bool success, string failureReason, ArenaWorldPoint playerSpawn)
        {
            Success = success;
            FailureReason = failureReason;
            PlayerSpawn = playerSpawn;
        }

        public bool Success { get; }

        public string FailureReason { get; }

        public ArenaWorldPoint PlayerSpawn { get; }

        public static HijackedArenaLoad Loaded(ArenaWorldPoint playerSpawn) =>
            new HijackedArenaLoad(true, null, playerSpawn);

        public static HijackedArenaLoad Failed(string reason) =>
            new HijackedArenaLoad(false, reason, default);
    }

    /// <summary>
    /// Produces the <see cref="Room"/> that a hijacked level load places, on demand, from the same shipped arena
    /// content the additive path uses.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the delegates.</b> Loading the arena means the AssetBundle + artifact flow, which lives in
    /// <c>FalseGods.UnityRuntime</c> — an assembly this adapter deliberately cannot reference. The Composition
    /// Root, which can see both, supplies the load and release as callbacks and a way to reach the realized root,
    /// the same shape the navigation and vanilla-material adapters already take. So content loading stays where
    /// it belongs and the game-type dressing stays here.</para>
    /// <para><b>One arena at a time.</b> Acquiring releases whatever the previous hijacked load held first. By
    /// then the previous level — and with it the previous arena, which the level owned — has already been torn
    /// down, so releasing its bundle is safe.</para>
    /// </remarks>
    public sealed class HijackedArenaRoomSource
    {
        private readonly Func<HijackedArenaLoad> _load;
        private readonly Func<GameObject> _realizedRoot;
        private readonly Action _release;
        private readonly ILogger _logger;

        private bool _holdsContent;

        public HijackedArenaRoomSource(
            Func<HijackedArenaLoad> load, Func<GameObject> realizedRoot, Action release, ILogger logger = null)
        {
            _load = load ?? throw new ArgumentNullException(nameof(load));
            _realizedRoot = realizedRoot ?? throw new ArgumentNullException(nameof(realizedRoot));
            _release = release ?? throw new ArgumentNullException(nameof(release));
            _logger = logger;
        }

        /// <summary>
        /// Load the arena content and dress it as a room. Returns null when the content is unavailable or
        /// invalid — the caller then leaves the level's own start area to generate, so a content problem costs an
        /// ordinary cave level rather than a broken load.
        /// </summary>
        public Room Acquire()
        {
            Release();

            HijackedArenaLoad loaded;
            try
            {
                loaded = _load();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arena-room] arena content threw while loading: {exception}");
                return null;
            }

            if (loaded == null || !loaded.Success)
            {
                _logger?.LogWarning($"[arena-room] arena content unavailable: {loaded?.FailureReason ?? "no result"}");
                return null;
            }

            _holdsContent = true;

            var root = _realizedRoot();
            if (root == null)
            {
                _logger?.LogWarning("[arena-room] arena content loaded but produced no realized hierarchy.");
                Release();
                return null;
            }

            try
            {
                var spawn = loaded.PlayerSpawn;
                return SulfurArenaRoom.Build(root, new Vector3(spawn.X, spawn.Y, spawn.Z), _logger);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arena-room] arena could not be dressed as a room: {exception}");
                Release();
                return null;
            }
        }

        /// <summary>Release the arena content this source holds. Idempotent.</summary>
        public void Release()
        {
            if (!_holdsContent)
            {
                return;
            }

            _holdsContent = false;
            try
            {
                _release();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arena-room] releasing arena content threw: {exception}");
            }
        }
    }
}
