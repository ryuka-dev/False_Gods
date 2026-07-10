namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// An authored axis-aligned bounding volume (centre + size), used by navigation authoring in the content
    /// hash (Docs/MultiplayerLoadingContract.md §5.2.1, input 7). Both components are quantised as lengths.
    /// </summary>
    public sealed record AuthoredBounds(Vector3 Center, Vector3 Size);
}
