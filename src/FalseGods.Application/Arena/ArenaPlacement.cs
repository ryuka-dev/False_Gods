using System;
using System.Collections.Generic;
using FalseGods.Protocol.Arena;
using FalseGods.RuntimeContracts.Arena;

namespace FalseGods.Application.Arena
{
    /// <summary>
    /// Chooses the host's arena origin: the world position at which the authored player-spawn marker lands
    /// exactly at the local player's feet — the measured P4 pattern (the arena is placed around the player;
    /// the player is never moved, because the game's own seal/teleport is not yet bridged).
    /// </summary>
    /// <remarks>
    /// The origin is derived from <b>authored</b> data (the parity map's player-spawn offset), so it exists
    /// before anything is realized; the host then broadcasts it in <c>EnterArena</c> and every peer realizes at
    /// the same world coordinates. Single-player uses it identically — one sequence, no second path (§5.3).
    /// <para>
    /// The offset walks the marker's parity-map ancestor chain summing local positions, which is only valid
    /// while every ancestor has identity rotation and unit scale — true of the authored root layout, and
    /// asserted loudly rather than silently mis-placing the arena if authoring ever changes.
    /// </para>
    /// </remarks>
    public static class ArenaPlacement
    {
        private const float IdentityEpsilon = 1e-4f;

        /// <summary>The origin that puts the authored <see cref="ArenaMarkerKinds.Player"/> marker at
        /// <paramref name="playerFoot"/>. Throws <see cref="InvalidOperationException"/> when the authored data
        /// cannot support the computation — the caller aborts the encounter before anything is sent.</summary>
        public static ArenaWorldPoint OriginForPlayerFoot(ArenaContentArtifact artifact, ArenaWorldPoint playerFoot)
        {
            if (artifact is null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            var offset = RootRelativeMarkerOffset(artifact, ArenaMarkerKinds.Player);
            return new ArenaWorldPoint(playerFoot.X - offset.X, playerFoot.Y - offset.Y, playerFoot.Z - offset.Z);
        }

        private static ArenaWorldPoint RootRelativeMarkerOffset(ArenaContentArtifact artifact, string kind)
        {
            ArenaParityNode? marker = null;
            var byPath = new Dictionary<string, ArenaParityNode>(StringComparer.Ordinal);
            foreach (var node in artifact.Parity)
            {
                byPath[node.Path] = node;
                if (marker is null && string.Equals(node.Kind, kind, StringComparison.Ordinal))
                {
                    marker = node;
                }
            }

            if (marker is null)
            {
                throw new InvalidOperationException($"The authored parity map has no '{kind}' marker to place the arena by.");
            }

            // Sum local positions along the ancestor chain (path segments are '/'-separated, root-relative).
            var x = 0f;
            var y = 0f;
            var z = 0f;
            var segments = marker.Path.Split('/');
            var path = string.Empty;
            for (var i = 0; i < segments.Length; i++)
            {
                path = i == 0 ? segments[0] : path + "/" + segments[i];
                if (!byPath.TryGetValue(path, out var node))
                {
                    // An ancestor absent from the parity map cannot be verified as identity; refuse loudly.
                    throw new InvalidOperationException(
                        $"Cannot derive the '{kind}' marker offset: ancestor '{path}' is not in the parity map.");
                }

                // Only an ANCESTOR's rotation/scale would bend the summed offset; the marker's own orientation
                // does not affect its position.
                if (i < segments.Length - 1)
                {
                    RequireIdentityOrientation(node);
                }

                x += node.LocalTransform.Position.X;
                y += node.LocalTransform.Position.Y;
                z += node.LocalTransform.Position.Z;
            }

            return new ArenaWorldPoint(x, y, z);
        }

        private static void RequireIdentityOrientation(ArenaParityNode node)
        {
            var rotation = node.LocalTransform.Rotation;
            var rotationIdentity =
                Math.Abs(rotation.X) <= IdentityEpsilon
                && Math.Abs(rotation.Y) <= IdentityEpsilon
                && Math.Abs(rotation.Z) <= IdentityEpsilon
                && Math.Abs(Math.Abs(rotation.W) - 1f) <= IdentityEpsilon;

            var scale = node.LocalTransform.Scale;
            var scaleUnit =
                Math.Abs(scale.X - 1f) <= IdentityEpsilon
                && Math.Abs(scale.Y - 1f) <= IdentityEpsilon
                && Math.Abs(scale.Z - 1f) <= IdentityEpsilon;

            if (!rotationIdentity || !scaleUnit)
            {
                throw new InvalidOperationException(
                    $"Cannot derive a marker offset by summing positions: '{node.Path}' has a non-identity "
                    + "rotation or non-unit scale. Extend ArenaPlacement with full transform composition first.");
            }
        }
    }
}
