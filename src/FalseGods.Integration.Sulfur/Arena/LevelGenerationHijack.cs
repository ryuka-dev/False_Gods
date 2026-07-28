using System;
using System.Collections.Generic;
using LevelGeneration;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>How far a hijacked level's fog reaches: where it starts thickening and where it becomes opaque.
    /// The fog colour is deliberately not part of this — that stays the level's own.</summary>
    public sealed class ArenaFogRange
    {
        public ArenaFogRange(float startDistance, float endDistance)
        {
            StartDistance = startDistance;
            EndDistance = endDistance;
        }

        public float StartDistance { get; }

        public float EndDistance { get; }
    }

    /// <summary>One level of one environment — the granularity at which a level either is our arena or is an
    /// ordinary level. A struct so "no arena mode" can be an honest <c>null</c>.</summary>
    public readonly struct ArenaLevel : IEquatable<ArenaLevel>
    {
        public ArenaLevel(WorldEnvironmentIds environment, int levelIndex)
        {
            Environment = environment;
            LevelIndex = levelIndex;
        }

        public WorldEnvironmentIds Environment { get; }

        public int LevelIndex { get; }

        public bool Equals(ArenaLevel other) =>
            Environment == other.Environment && LevelIndex == other.LevelIndex;

        public override bool Equals(object obj) => obj is ArenaLevel other && Equals(other);

        public override int GetHashCode() => ((int)Environment * 397) ^ LevelIndex;

        public override string ToString() => $"{Environment} level {LevelIndex}";
    }

    /// <summary>
    /// The single owner of the two Strategy A states: <b>which level this peer wants to be the arena</b>
    /// (<see cref="ArenaMode"/>), and <b>whether the generation run happening right now is building it</b>
    /// (<see cref="IsArmed"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a static.</b> Harmony patch methods are static, so the state they consult has to be reachable
    /// statically. Keeping it in one place with explicit transitions — rather than a bare mutable field next to
    /// the patches — keeps the ownership question answerable. Nothing else writes it.</para>
    /// <para><b>Why a mode rather than a one-shot arm.</b> Arming immediately before <i>our own</i> level-load
    /// request only covers the loads this peer initiates, and in a session that is not the interesting set.
    /// Measured 2026-07-26 with two peers: SULFUR Together does not auto-follow the host's level, and a
    /// <i>client</i>-initiated level load is intercepted and relayed so the <b>host</b> leads the transition and
    /// the client then re-loads under the host's seed. So one press of the developer key produces up to three
    /// generation runs across the two peers, and a one-shot arm covered exactly one of them — whichever peer
    /// pressed the key got the arena and the other got an ordinary cave. The mode is a standing declaration
    /// ("while I am in arena mode, that level IS the arena on this peer"), so every path that generates it —
    /// our key, a peer following the host, the host leading a client's transition — builds the same arena.</para>
    /// <para><b>Still scoped to one generation run.</b> The mode decides <i>whether</i> to arm; the arming itself
    /// is per-run. It happens at the canonical boundary — the start of <c>MakerGraphContext.StartMaking</c>,
    /// which is one whole level-generation graph — and is released when that run ends, including on failure
    /// (the wrapper disarms in a <c>finally</c>). A generation of any other level, mode or not, is untouched.</para>
    /// <para><b>The mode lasts one visit, not forever.</b> The same boundary withdraws it: generating a
    /// <i>different</i> level is the game saying the players have left, and a declaration that outlived that
    /// would make the level it names unplayable in its ordinary form for the rest of the process. Every peer
    /// generates the same levels, so every peer withdraws at the same point; the host additionally broadcasts
    /// the withdrawal so a peer that was not generating cannot be left holding it.</para>
    /// <para><b>Neutered nodes.</b> Our arena is a single sealed room: the level must not grow a main path, side
    /// rooms, wandering enemies, or events around it. Those four generation steps are skipped while armed; every
    /// other step — notably navigation building and player spawning — runs natively, which is the entire point of
    /// Strategy A. Nodes that already no-op on our content (barricades and loot need connectors and containers we
    /// do not have) are deliberately left alone: the fewer steps we override, the less of the game's own level
    /// pipeline we have to keep working.</para>
    /// </remarks>
    public static class LevelGenerationHijack
    {
        // Skipped while armed. Types, not names: a rename in a game update becomes a compile error here rather
        // than a silent no-op at runtime.
        private static readonly HashSet<Type> NeuteredNodes = new HashSet<Type>
        {
            typeof(CreateMainPathNode),
            typeof(AddExtraRoomsNode),
            typeof(SpawnEnemiesNode),
            typeof(SpawnEventsNode),
        };

        /// <summary>True while the generation run happening right now is building our arena. Read by the
        /// generation hooks; set per run by <see cref="TryArmForRun"/>.</summary>
        public static bool IsArmed { get; private set; }

        /// <summary>
        /// The level this peer currently wants to be the arena, or null when the peer is playing the game
        /// normally. A standing declaration, deliberately not tied to any one load request.
        /// </summary>
        public static ArenaLevel? ArenaMode { get; private set; }

        /// <summary>Whether this peer is currently declaring a level to be the arena.</summary>
        public static bool IsArenaModeOn => ArenaMode != null;

        /// <summary>
        /// Where a hijacked load gets its arena room. Installed once by the Composition Root; when absent, a
        /// hijacked load simply generates the level's own start area, which is the safe way to be misconfigured.
        /// </summary>
        public static HijackedArenaRoomSource? ArenaRooms { get; set; }

        /// <summary>
        /// The fog range a hijacked level should use, or null to leave the level's own alone. A boss arena is far
        /// wider than the corridor-sized rooms the cave environment's fog cutoff is tuned for, so without this the
        /// walls are simply not visible from the middle of it.
        /// </summary>
        public static ArenaFogRange? Fog { get; set; }

        /// <summary>Diagnostics only — never required for correct behaviour.</summary>
        public static ILogger? Logger { get; set; }

        /// <summary>Declare <paramref name="level"/> to be the arena on this peer until further notice. Every
        /// generation of that level from now on builds the arena, whoever asked for it.</summary>
        public static void EnterArenaMode(ArenaLevel level)
        {
            if (ArenaMode != null && ArenaMode.Value.Equals(level))
            {
                return; // already declared; re-declaring says nothing new
            }

            ArenaMode = level;
            Logger?.Log($"[levelgen] arena mode ON for {level}; every generation of that level on this peer "
                + "builds the boss arena, whoever asked for it.");
        }

        /// <summary>Stop declaring any level to be the arena. A level already standing is left alone — it is the
        /// next generation that goes back to being an ordinary one.</summary>
        /// <param name="because">Why, for the log; omitted when it was simply asked for.</param>
        public static void LeaveArenaMode(string? because = null)
        {
            if (ArenaMode == null)
            {
                return;
            }

            Logger?.Log($"[levelgen] arena mode OFF (was {ArenaMode}"
                + $"{(because == null ? string.Empty : $"; {because}")}); that level generates normally again.");
            ArenaMode = null;
        }

        /// <summary>
        /// Decide whether the generation run that is starting builds the arena: it does exactly when this peer is
        /// in arena mode for the level being generated. Returns whether it armed.
        /// </summary>
        public static bool TryArmForRun(ArenaLevel generating)
        {
            var mode = ArenaMode;
            if (mode == null || !mode.Value.Equals(generating))
            {
                return false;
            }

            IsArmed = true;
            return true;
        }

        /// <summary>
        /// How many generation runs that built our arena have finished. Rises once per arena the players are put
        /// into, and never falls.
        /// </summary>
        /// <remarks>
        /// <b>The difference between "the arena object exists" and "the level around it is finished".</b> The arena
        /// is instantiated at generation step 3 of seventeen — before navigation is scanned and before the player
        /// is even placed — so anything that waits for the arena to <i>appear</i> starts far too early. The end of
        /// the run is the canonical "the players are in the room" moment, and it is already a boundary this class
        /// owns, so counting it here costs nothing and needs no observer of its own.
        /// </remarks>
        public static int ArenaRunsFinished { get; private set; }

        /// <summary>Disarm, whether the generation run completed, failed, or was abandoned. Idempotent. The arena
        /// <i>mode</i> is untouched — it outlives any one run, which is the whole point of it.</summary>
        public static void Disarm()
        {
            if (IsArmed)
            {
                // Counted even for a run that failed: what a failed run leaves behind is a level with no arena in
                // it, and IsLive answers that separately. This only says the generating is over.
                ArenaRunsFinished++;
            }

            IsArmed = false;
        }

        /// <summary>Whether this generation step is one of the ones our single-room arena replaces.</summary>
        public static bool IsNeutered(Type nodeType) => nodeType != null && NeuteredNodes.Contains(nodeType);
    }
}
