namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// The small header peers exchange to prove they realised the same arena — modelled on SULFUR Together's
    /// <c>NetLevelManifestHeader</c> (Docs/MultiplayerLoadingContract.md §5.2).
    /// </summary>
    /// <remarks>
    /// <see cref="ArenaId"/> + <see cref="ArenaVersion"/> <em>name</em> the layout; the
    /// <c>(ContentHashSchemaVersion, ContentHash)</c> pair <em>proves</em> two peers built the same one (a stale
    /// or tampered bundle has a matching version but a different hash). Every field is untrusted input on
    /// receipt, and any mismatch is a hard, explicit refusal — never a silent divergence (§5.2, §5.3.1).
    /// <para>
    /// This is the fixed-arena shape. The procedural-arena fields (<c>Seed</c>, host-decided <c>Modules[]</c>)
    /// from §5.2 are deliberately omitted until a procedural arena exists to consume them — a field with no
    /// consumer is premature (Docs/RiskList.md R31). Adding them is a schema-version change.
    /// </para>
    /// </remarks>
    public sealed record ArenaManifest(
        string ArenaId,
        int ArenaVersion,
        ContentHashSchemaVersion ContentHashSchemaVersion,
        ContentHash ContentHash,
        int ProtocolVersion,
        string BundleVersion);
}
