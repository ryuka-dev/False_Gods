using System;

namespace FalseGods.Protocol.Wire
{
    /// <summary>
    /// The identity of one generated level: which environment (chapter) it belongs to and its index within that
    /// environment.
    /// </summary>
    /// <remarks>
    /// Deliberately two plain integers rather than the game's environment enum: <c>FalseGods.Protocol</c> holds no
    /// game reference (Docs/Architecture.md §3), and a wire contract should not change shape because a game update
    /// renumbers an enum. The integration layer maps it to and from the game's own type, and treats an environment
    /// value it does not recognise as untrusted input rather than a cast.
    /// </remarks>
    public readonly struct ArenaLevelId : IEquatable<ArenaLevelId>
    {
        public ArenaLevelId(int environment, int levelIndex)
        {
            Environment = environment;
            LevelIndex = levelIndex;
        }

        public int Environment { get; }

        public int LevelIndex { get; }

        public bool Equals(ArenaLevelId other) =>
            Environment == other.Environment && LevelIndex == other.LevelIndex;

        public override bool Equals(object? obj) => obj is ArenaLevelId other && Equals(other);

        public override int GetHashCode() => (Environment * 397) ^ LevelIndex;

        public override string ToString() => $"env {Environment} level {LevelIndex}";

        public static bool operator ==(ArenaLevelId left, ArenaLevelId right) => left.Equals(right);

        public static bool operator !=(ArenaLevelId left, ArenaLevelId right) => !left.Equals(right);
    }

    /// <summary>
    /// Host → all peers, reliable-ordered: whether <see cref="Level"/> is a False Gods boss arena. A
    /// <b>standing</b> declaration, not an instruction to load anything — it says what that level <i>is</i> from
    /// now on, and every peer that generates it afterwards builds the arena instead of the game's own content.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it must be replicated at all.</b> Each peer generates every level itself. Transitions are led
    /// by the host and followed by the others, so most generation runs on a machine were asked for by somebody
    /// else; a peer that does not know what that level is builds an ordinary one and the session splits into two
    /// different rooms. Declaring it is the only way every peer's generation agrees.</para>
    /// <para><b>Why the host owns it.</b> The host is authoritative for the world and for level transitions, so it
    /// is also the one that decides a level is a boss arena. A peer that wants the session to go there sends an
    /// <see cref="ArenaLevelRequested"/> and lets the host decide — the same shape the game's own session layer
    /// uses for a client-initiated transition.</para>
    /// <para>Sent when the declaration changes and to each peer that joins afterwards, so a late joiner is never
    /// the only one who does not know. Untrusted input on receipt: accepted only from the session host, and only
    /// for an environment the receiver recognises.</para>
    /// </remarks>
    public sealed record ArenaLevelDeclared(
        ArenaLevelId Level,
        bool IsBossArena);

    /// <summary>
    /// Peer → host, reliable-ordered: this peer asks the session to go to the boss arena at <see cref="Level"/>.
    /// A request, not a transition — the host declares and leads, or ignores it.
    /// </summary>
    /// <remarks>Untrusted input on receipt: accepted only by the host, and only from a session member.</remarks>
    public sealed record ArenaLevelRequested(
        ArenaLevelId Level);
}
