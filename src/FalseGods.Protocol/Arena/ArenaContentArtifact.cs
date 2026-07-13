using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FalseGods.Protocol.Arena
{
    /// <summary>
    /// The shippable, self-describing arena content artifact: the authored <see cref="ArenaContentDefinition"/>
    /// (from which the canonical <see cref="ContentHash"/> is derived — R34) plus the manifest stamps and a
    /// runtime-parity map (R14). One editor export produces it; the runtime reads it back verbatim
    /// (Docs/OriginalContentPipeline.md §8.6, Docs/MultiplayerLoadingContract.md §5.2).
    /// </summary>
    /// <remarks>
    /// <para><b>Why a text artifact and not the hash alone.</b> The runtime needs two different things from
    /// authoring: the authored inputs to <em>recompute</em> the hash locally (never trusting a shipped hash), and
    /// an authored manifest to <em>verify the realized hierarchy against</em> (R14). Both come from this one
    /// document.</para>
    /// <para><b>The format is a transport, not the hash.</b> The canonical, cross-machine-stable encoding is
    /// <see cref="ContentHashComputer"/>'s (marker-sorted, integer-quantized). This artifact only has to
    /// round-trip the authored floats losslessly (round-trippable "R" formatting) so the runtime rebuilds the
    /// exact same <see cref="ArenaContentDefinition"/> and therefore the exact same hash — the artifact's own
    /// field order, spacing, or list order never feed the hash.</para>
    /// <para><b>Deliberately dependency-free.</b> Lives in <c>FalseGods.Protocol</c> (net472, no serialization
    /// package) so the runtime plugin and the unit tests share one reader. The Unity editor exporter, which
    /// cannot reference this assembly (Unity is .NET&nbsp;Standard), writes the same line format independently;
    /// the runtime proves format agreement by parsing the shipped artifact, and a fixture test pins the shape.</para>
    /// </remarks>
    public sealed record ArenaContentArtifact(
        ArenaContentDefinition Definition,
        ContentHashSchemaVersion SchemaVersion,
        int ProtocolVersion,
        string BundleVersion,
        IReadOnlyList<ArenaParityNode> Parity)
    {
        /// <summary>Bumped only when the artifact's on-disk shape changes — independent of
        /// <see cref="ContentHashSchemaVersion"/>, which versions the hash inputs. A reader rejects an unknown
        /// artifact-format version rather than mis-parsing it.</summary>
        public const int ArtifactFormatVersion = 1;

        private const string Magic = "FGARENA";
        private const char Sep = '\t';
        private const string Nil = "-";

        /// <summary>Recompute the canonical content hash from the authored inputs under this artifact's schema
        /// version. Throws <see cref="ArenaContentExportException"/> on non-reproducible authored data.</summary>
        public ContentHash ComputeContentHash() => ContentHashComputer.Compute(Definition, SchemaVersion);

        /// <summary>The small header peers exchange, with the hash computed from this artifact's inputs.</summary>
        public ArenaManifest ToManifest() => new ArenaManifest(
            Definition.ArenaId, Definition.ArenaVersion, SchemaVersion, ComputeContentHash(),
            ProtocolVersion, BundleVersion);

        // ─────────────────────────────────────────── write ────────────────────────────────────────────────

        public string Serialize()
        {
            var sb = new StringBuilder();
            Row(sb, Magic, ArtifactFormatVersion.ToString(CultureInfo.InvariantCulture));
            Row(sb, "arenaId", Definition.ArenaId);
            Row(sb, "arenaVersion", Int(Definition.ArenaVersion));
            Row(sb, "arenaContentId", Definition.ArenaContentId);
            Row(sb, "schemaVersion", Int(SchemaVersion.Value));
            Row(sb, "protocolVersion", Int(ProtocolVersion));
            Row(sb, "bundleVersion", BundleVersion);

            foreach (var n in Definition.Nodes)
                Row(sb, Fields("node", n.MarkerId.ToCanonicalString(), n.NodeKind, Marker(n.ParentMarkerId), Transform(n.LocalTransform)));
            foreach (var p in Definition.VanillaProxies)
                Row(sb, Fields("proxy", p.MarkerId.ToCanonicalString(), p.AddressableKeyOrGuid, Transform(p.LocalTransform)));
            foreach (var c in Definition.Colliders)
                Row(sb, Fields("collider", c.MarkerId.ToCanonicalString(), c.ColliderKind, c.LayerName, Geometry(c.GeometryParameters)));
            foreach (var nav in Definition.NavDefinitions)
                Row(sb, Fields("nav", nav.MarkerId.ToCanonicalString(), nav.NavKind, nav.NavmeshPrefabContentId ?? Nil, Bounds(nav.Bounds)));
            foreach (var s in Definition.Spawns)
                Row(sb, Fields("spawn", s.MarkerId.ToCanonicalString(), s.SpawnKind, s.DefinitionId, Transform(s.LocalTransform)));
            foreach (var m in Definition.Mechanisms)
                Row(sb, Fields("mechanism", m.MarkerId.ToCanonicalString(), m.MechanismDefinitionId, m.MechanismGroupId, Transform(m.LocalTransform)));

            foreach (var parity in Parity)
                Row(sb, Fields("parity", parity.Path, parity.Kind, Transform(parity.LocalTransform)));

            return sb.ToString();
        }

        // Each argument is either a single field (string) or an already-expanded group of fields (string[]);
        // flatten them into one flat field list so a transform contributes ten fields, not one tab-laden one.
        private static string[] Fields(params object[] parts)
        {
            var flat = new List<string>();
            foreach (var part in parts)
            {
                if (part is string[] group) flat.AddRange(group);
                else flat.Add((string)part);
            }
            return flat.ToArray();
        }

        private static void Row(StringBuilder sb, params string[] fields)
        {
            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(Sep);
                var f = fields[i];
                if (f.IndexOf(Sep) >= 0 || f.IndexOf('\n') >= 0)
                    throw new ArenaContentExportException($"Artifact field contains a tab or newline: '{f}'.");
                sb.Append(f);
            }
            sb.Append('\n');
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Flt(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Dbl(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Marker(StableMarkerId? m) => m.HasValue ? m.Value.ToCanonicalString() : Nil;

        private static string[] Transform(AuthoredTransform t) => new[]
        {
            Flt(t.Position.X), Flt(t.Position.Y), Flt(t.Position.Z),
            Flt(t.Rotation.X), Flt(t.Rotation.Y), Flt(t.Rotation.Z), Flt(t.Rotation.W),
            Flt(t.Scale.X), Flt(t.Scale.Y), Flt(t.Scale.Z),
        };

        private static string[] Bounds(AuthoredBounds b) => new[]
        {
            Flt(b.Center.X), Flt(b.Center.Y), Flt(b.Center.Z),
            Flt(b.Size.X), Flt(b.Size.Y), Flt(b.Size.Z),
        };

        private static string[] Geometry(IReadOnlyList<double> parameters) =>
            new[] { Int(parameters.Count) }.Concat(parameters.Select(Dbl)).ToArray();

        // ─────────────────────────────────────────── read ─────────────────────────────────────────────────

        public static ArenaContentArtifact Parse(string text)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            var rows = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Where(line => line.Length > 0)
                .Select(line => line.Split(Sep))
                .ToList();
            var cursor = 0;

            string[] Next(string expectedTag, int minFields)
            {
                if (cursor >= rows.Count)
                    throw Malformed($"expected a '{expectedTag}' row but the artifact ended.");
                var row = rows[cursor++];
                if (row[0] != expectedTag)
                    throw Malformed($"expected a '{expectedTag}' row, found '{row[0]}'.");
                if (row.Length < minFields)
                    throw Malformed($"'{expectedTag}' row has {row.Length} fields, needs at least {minFields}.");
                return row;
            }

            var header = Next(Magic, 2);
            var formatVersion = ParseInt(header[1], "artifact format version");
            if (formatVersion != ArtifactFormatVersion)
                throw Malformed($"unsupported artifact format version {formatVersion} (this reader is {ArtifactFormatVersion}).");

            var arenaId = Next("arenaId", 2)[1];
            var arenaVersion = ParseInt(Next("arenaVersion", 2)[1], "arenaVersion");
            var arenaContentId = Next("arenaContentId", 2)[1];
            var schemaVersion = ParseInt(Next("schemaVersion", 2)[1], "schemaVersion");
            var protocolVersion = ParseInt(Next("protocolVersion", 2)[1], "protocolVersion");
            var bundleVersion = Next("bundleVersion", 2)[1];

            var nodes = new List<AuthoredNode>();
            var proxies = new List<VanillaProxyDefinition>();
            var colliders = new List<ColliderDefinition>();
            var navs = new List<NavDefinition>();
            var spawns = new List<SpawnDefinition>();
            var mechanisms = new List<MechanismDefinition>();
            var parity = new List<ArenaParityNode>();

            for (; cursor < rows.Count; cursor++)
            {
                var row = rows[cursor];
                switch (row[0])
                {
                    case "node":
                        Require(row, 14, "node");
                        nodes.Add(new AuthoredNode(Marker(row[1], "node"), row[2], OptionalMarker(row[3], "node parent"),
                            ReadTransform(row, 4, "node")));
                        break;
                    case "proxy":
                        Require(row, 13, "proxy");
                        proxies.Add(new VanillaProxyDefinition(Marker(row[1], "proxy"), row[2], ReadTransform(row, 3, "proxy")));
                        break;
                    case "collider":
                        Require(row, 5, "collider");
                        colliders.Add(new ColliderDefinition(Marker(row[1], "collider"), row[2],
                            ReadGeometry(row, 4, "collider"), row[3]));
                        break;
                    case "nav":
                        Require(row, 10, "nav");
                        navs.Add(new NavDefinition(Marker(row[1], "nav"), row[2],
                            ReadBounds(row, 4, "nav"), row[3] == Nil ? null : row[3]));
                        break;
                    case "spawn":
                        Require(row, 14, "spawn");
                        spawns.Add(new SpawnDefinition(Marker(row[1], "spawn"), row[2], row[3], ReadTransform(row, 4, "spawn")));
                        break;
                    case "mechanism":
                        Require(row, 14, "mechanism");
                        mechanisms.Add(new MechanismDefinition(Marker(row[1], "mechanism"), row[2], row[3],
                            ReadTransform(row, 4, "mechanism")));
                        break;
                    case "parity":
                        Require(row, 13, "parity");
                        parity.Add(new ArenaParityNode(row[1], row[2], ReadTransform(row, 3, "parity")));
                        break;
                    default:
                        throw Malformed($"unknown row tag '{row[0]}'.");
                }
            }

            var definition = new ArenaContentDefinition(arenaId, arenaVersion, arenaContentId,
                nodes, proxies, colliders, navs, spawns, mechanisms);
            return new ArenaContentArtifact(definition, new ContentHashSchemaVersion(schemaVersion),
                protocolVersion, bundleVersion, parity);
        }

        private static void Require(string[] row, int minFields, string tag)
        {
            if (row.Length < minFields)
                throw Malformed($"'{tag}' row has {row.Length} fields, needs at least {minFields}.");
        }

        private static StableMarkerId Marker(string token, string where)
        {
            if (!Guid.TryParseExact(token, "D", out var guid))
                throw Malformed($"{where} marker '{token}' is not a canonical 'D'-form GUID.");
            return new StableMarkerId(guid);
        }

        private static StableMarkerId? OptionalMarker(string token, string where) =>
            token == Nil ? (StableMarkerId?)null : Marker(token, where);

        private static AuthoredTransform ReadTransform(string[] row, int at, string where) =>
            new AuthoredTransform(
                new Vector3(ParseFloat(row[at], where), ParseFloat(row[at + 1], where), ParseFloat(row[at + 2], where)),
                new Quaternion(ParseFloat(row[at + 3], where), ParseFloat(row[at + 4], where),
                    ParseFloat(row[at + 5], where), ParseFloat(row[at + 6], where)),
                new Vector3(ParseFloat(row[at + 7], where), ParseFloat(row[at + 8], where), ParseFloat(row[at + 9], where)));

        private static AuthoredBounds ReadBounds(string[] row, int at, string where) =>
            new AuthoredBounds(
                new Vector3(ParseFloat(row[at], where), ParseFloat(row[at + 1], where), ParseFloat(row[at + 2], where)),
                new Vector3(ParseFloat(row[at + 3], where), ParseFloat(row[at + 4], where), ParseFloat(row[at + 5], where)));

        private static IReadOnlyList<double> ReadGeometry(string[] row, int at, string where)
        {
            var count = ParseInt(row[at], $"{where} geometry count");
            if (count < 0 || at + 1 + count > row.Length)
                throw Malformed($"{where} geometry count {count} does not fit the row.");
            var values = new double[count];
            for (var i = 0; i < count; i++)
                values[i] = ParseDouble(row[at + 1 + i], $"{where} geometry[{i}]");
            return values;
        }

        private static int ParseInt(string token, string where) =>
            int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v : throw Malformed($"{where} '{token}' is not an integer.");

        private static float ParseFloat(string token, string where) =>
            float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : throw Malformed($"{where} '{token}' is not a float.");

        private static double ParseDouble(string token, string where) =>
            double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : throw Malformed($"{where} '{token}' is not a number.");

        private static ArenaContentExportException Malformed(string detail) =>
            new ArenaContentExportException($"Malformed arena content artifact: {detail}");
    }

    /// <summary>
    /// One runtime-parity target (R14): the hierarchy <see cref="Path"/> at which the realized arena must carry
    /// a node of this <see cref="Kind"/> with this authored <see cref="LocalTransform"/>. The path is used
    /// <em>only</em> to locate the node at runtime for the parity check — it is never an input to the content
    /// hash (which orders by <see cref="StableMarkerId"/>, never by path).
    /// </summary>
    public sealed record ArenaParityNode(string Path, string Kind, AuthoredTransform LocalTransform);
}
