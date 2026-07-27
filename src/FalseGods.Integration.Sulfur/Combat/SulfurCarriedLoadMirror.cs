using System;
using System.Collections.Generic;
using FalseGods.RuntimeContracts.Arena;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// Puts the load on the backs of a client's goblins: the carriers themselves are the game's own units and
    /// mirror for free, but what they are <i>holding</i> is ours and does not.
    /// </summary>
    /// <remarks>
    /// <para><b>Cosmetic, and only cosmetic.</b> The crates that matter — the ones resting on piles, the ones the
    /// boss fires — are kept in step by the carry commands themselves. This puts a stack on a goblin's back so a
    /// client sees the village working rather than a procession of empty-handed goblins walking past crates that
    /// appear and vanish on their own.</para>
    /// <para><b>Matched by proximity, deliberately.</b> A client has no name for the host's carriers: the session
    /// layer mirrors units, not our idea of who is on an errand, and there is no identity crossing that seam. So
    /// the goblin nearest where the host says a load was picked up is the one that gets the stack. That is sound
    /// in practice because carriers are spread along a route and only one is ever standing at a heap — and when it
    /// does pick wrong, a stack rides the wrong goblin for one leg and nothing else in the fight changes.</para>
    /// </remarks>
    public sealed class SulfurCarriedLoadMirror
    {
        /// <summary>How far from the reported spot a goblin may be and still be taken for the carrier. Wide enough
        /// to absorb the lag between the host's carrier and the client's copy of it, tight enough that a goblin
        /// elsewhere on the route is never mistaken for it.</summary>
        private const float MatchRadius = 8f;

        private const float StackSpacing = 0.55f;
        private const float StackBase = 1.9f;
        private const int MaxDrawnStack = 5;

        private readonly SulfurThrownCratePort _crates;
        private readonly ILogger _logger;
        private readonly List<Load> _loads = new List<Load>();

        public SulfurCarriedLoadMirror(SulfurThrownCratePort crates, ILogger logger = null)
        {
            _crates = crates ?? throw new ArgumentNullException(nameof(crates));
            _logger = logger;
        }

        /// <summary>The host says a load was picked up here: give the nearest goblin a stack to carry.</summary>
        public void PickedUp(ArenaWorldPoint at, int count)
        {
            Forget();
            if (count <= 0)
            {
                return;
            }

            var goblin = NearestCivilianTo(new Vector3(at.X, at.Y, at.Z));
            if (goblin == null)
            {
                return; // nobody there to have picked it up; the piles still agree, which is what matters
            }

            var load = Find(goblin);
            if (load == null)
            {
                load = new Load(goblin);
                _loads.Add(load);
            }

            _crates.TryGetLook(out var mesh, out var material);
            load.Show(Math.Min(count + load.Shown, MaxDrawnStack), mesh, material);
        }

        /// <summary>The host says a load left a carrier's hands here: take the stack off the nearest goblin.</summary>
        public void PutDown(ArenaWorldPoint from)
        {
            Forget();

            var goblin = NearestCivilianTo(new Vector3(from.X, from.Y, from.Z));
            if (goblin == null)
            {
                return;
            }

            var load = Find(goblin);
            load?.Clear();
        }

        /// <summary>Take every mirrored stack away — the fight is over, or this peer is no longer a client.</summary>
        public void Clear()
        {
            for (var i = 0; i < _loads.Count; i++)
            {
                _loads[i].Clear();
            }

            _loads.Clear();
        }

        private Load Find(Unit goblin)
        {
            for (var i = 0; i < _loads.Count; i++)
            {
                if (ReferenceEquals(_loads[i].Goblin, goblin))
                {
                    return _loads[i];
                }
            }

            return null;
        }

        /// <summary>Drop the stacks whose goblins have gone, so a dead carrier does not leave crates floating.</summary>
        private void Forget()
        {
            for (var i = _loads.Count - 1; i >= 0; i--)
            {
                if (_loads[i].Goblin == null || !_loads[i].Goblin.IsAlive)
                {
                    _loads[i].Clear();
                    _loads.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// The living goblin civilian closest to a point, within <see cref="MatchRadius"/>. Read off the game's own
        /// roster of live units, which is where the session layer puts the ones it mirrored.
        /// </summary>
        private static Unit NearestCivilianTo(Vector3 point)
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var npcs = gameManager != null ? gameManager.aliveNpcs : null;
            if (npcs == null)
            {
                return null;
            }

            Unit nearest = null;
            var nearestDistance = MatchRadius * MatchRadius;
            for (var i = 0; i < npcs.Count; i++)
            {
                var npc = npcs[i];
                if (npc == null || !npc.IsAlive || !npc.IsCivilian)
                {
                    continue;
                }

                var distance = (npc.transform.position - point).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = npc;
                }
            }

            return nearest;
        }

        /// <summary>One goblin's mirrored stack.</summary>
        private sealed class Load
        {
            private readonly List<GameObject> _drawn = new List<GameObject>();

            public Load(Unit goblin)
            {
                Goblin = goblin;
            }

            public Unit Goblin { get; }

            public int Shown => _drawn.Count;

            public void Show(int crates, Mesh look, Material lookMaterial)
            {
                if (Goblin == null)
                {
                    return;
                }

                while (_drawn.Count < crates)
                {
                    GameObject box;
                    if (look != null && lookMaterial != null)
                    {
                        box = new GameObject("FalseGods_CarriedCrate");
                        box.AddComponent<MeshFilter>().sharedMesh = look;
                        box.AddComponent<MeshRenderer>().sharedMaterial = lookMaterial;
                    }
                    else
                    {
                        box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        box.name = "FalseGods_CarriedCrate";
                        var collider = box.GetComponent<Collider>();
                        if (collider != null)
                        {
                            UnityEngine.Object.Destroy(collider);
                        }
                    }

                    box.transform.SetParent(Goblin.transform, false);
                    box.transform.localScale = Vector3.one * 0.5f;
                    box.transform.localPosition = new Vector3(0f, StackBase + _drawn.Count * StackSpacing, 0f);
                    _drawn.Add(box);
                }
            }

            public void Clear()
            {
                for (var i = 0; i < _drawn.Count; i++)
                {
                    if (_drawn[i] != null)
                    {
                        UnityEngine.Object.Destroy(_drawn[i]);
                    }
                }

                _drawn.Clear();
            }
        }
    }
}
