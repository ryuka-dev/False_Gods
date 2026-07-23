using System;
using System.Collections.Generic;
using LevelGeneration;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// The single owner of "a hijacked level load is in progress" — the flag that scopes every Strategy A
    /// level-generation hook to exactly the load we asked for, and to nothing else the player does.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a static.</b> Harmony patch methods are static, so the state they consult has to be reachable
    /// statically. Keeping it in one place with an explicit <see cref="Arm"/>/<see cref="Disarm"/> pair — rather
    /// than a bare mutable field next to the patches — keeps the ownership question answerable: the arena hijack
    /// port arms it immediately before asking the game to load the level, and the generation run itself disarms
    /// it when the run ends. Nothing else writes it.</para>
    /// <para><b>Scoped to one generation run.</b> Disarming is driven by the canonical boundary — the completion
    /// of <c>MakerGraphContext.StartMaking</c>, which is one whole level-generation graph — not by a timer or by
    /// guessing at the last node. A load that throws still disarms, because the wrapper disarms in a
    /// <c>finally</c>. So a subsequent ordinary level load generates completely untouched.</para>
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

        /// <summary>True while a level load we asked for is generating. Read by the generation hooks.</summary>
        public static bool IsArmed { get; private set; }

        /// <summary>
        /// Where a hijacked load gets its arena room. Installed once by the Composition Root; when absent, a
        /// hijacked load simply generates the level's own start area, which is the safe way to be misconfigured.
        /// </summary>
        public static HijackedArenaRoomSource? ArenaRooms { get; set; }

        /// <summary>Diagnostics only — never required for correct behaviour.</summary>
        public static ILogger? Logger { get; set; }

        /// <summary>Arm the hooks for the level load that is about to be requested.</summary>
        public static void Arm()
        {
            IsArmed = true;
        }

        /// <summary>Disarm, whether the generation run completed, failed, or was abandoned. Idempotent.</summary>
        public static void Disarm()
        {
            IsArmed = false;
        }

        /// <summary>Whether this generation step is one of the ones our single-room arena replaces.</summary>
        public static bool IsNeutered(Type nodeType) => nodeType != null && NeuteredNodes.Contains(nodeType);
    }
}
