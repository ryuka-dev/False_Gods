namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// An authored local transform (position, rotation, scale) as exported from the arena prefab.
    /// </summary>
    /// <remarks>
    /// "Local" is deliberate: the hash encodes each node's transform relative to its parent, never a world
    /// transform derived at load. The components are authored inputs; quantisation happens inside the content
    /// hash (Docs/MultiplayerLoadingContract.md §5.2.1).
    /// </remarks>
    public sealed record AuthoredTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale);
}
