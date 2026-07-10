using System;

namespace FalseGods.Protocol.Arena.Internal
{
    /// <summary>
    /// Turns authored floats into the deterministic integers the content hash encodes
    /// (Docs/MultiplayerLoadingContract.md §5.2.1, "Float quantization").
    /// </summary>
    /// <remarks>
    /// Floats never enter the hash. Getting this identical on every machine, GPU, and frame is the whole point
    /// of the ready gate — so the rules here are exact and their violation (NaN/infinity, zero-length
    /// quaternion) is an <see cref="ArenaContentExportException"/>, never a silently-different hash.
    /// </remarks>
    internal static class Quantizer
    {
        // Positions, scales, sizes, bounds: 0.1 mm resolution (value * 10_000), round-half-to-even, as int64.
        private const double LengthScale = 10_000d;

        // Rotations: quantise each component at 1e-6, i.e. multiply by 1_000_000, round-half-to-even.
        private const double RotationScale = 1_000_000d;

        /// <summary>Quantises a length-like value (position/scale/size/bounds component).</summary>
        public static long QuantizeLength(double value, string context)
        {
            var finite = RequireFinite(value, context);
            return QuantizeAt(NormalizeZero(finite), LengthScale);
        }

        /// <summary>
        /// Canonicalises and quantises a rotation. Normalises the quaternion, collapses <c>q</c> and <c>-q</c>
        /// (the same rotation) onto one representation via the lexicographic sign rule over (w, x, y, z), then
        /// quantises each component. Returned in encoding order: w, x, y, z.
        /// </summary>
        public static (long W, long X, long Y, long Z) QuantizeRotation(Quaternion rotation, string context)
        {
            var x = RequireFinite(rotation.X, context);
            var y = RequireFinite(rotation.Y, context);
            var z = RequireFinite(rotation.Z, context);
            var w = RequireFinite(rotation.W, context);

            var magnitude = Math.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
            if (magnitude == 0d)
            {
                throw new ArenaContentExportException(
                    $"Zero-length quaternion at {context}: a rotation with no magnitude has no canonical " +
                    "representation. Fix the authored rotation (identity is (0,0,0,1)).");
            }

            x = NormalizeZero(x / magnitude);
            y = NormalizeZero(y / magnitude);
            z = NormalizeZero(z / magnitude);
            w = NormalizeZero(w / magnitude);

            // First non-zero component in (w, x, y, z); if it is negative, negate the whole quaternion so that
            // q and -q collapse to one representation (including the (0,0,0,±1) case).
            var lead = w != 0d ? w : x != 0d ? x : y != 0d ? y : z;
            if (lead < 0d)
            {
                x = NormalizeZero(-x);
                y = NormalizeZero(-y);
                z = NormalizeZero(-z);
                w = NormalizeZero(-w);
            }

            return (
                QuantizeAt(w, RotationScale),
                QuantizeAt(x, RotationScale),
                QuantizeAt(y, RotationScale),
                QuantizeAt(z, RotationScale));
        }

        private static long QuantizeAt(double value, double scale) =>
            (long)Math.Round(value * scale, MidpointRounding.ToEven);

        // -0.0 and +0.0 are equal but not bit-identical; collapse to +0.0 before quantising so a negated zero
        // never encodes differently from a plain zero.
        private static double NormalizeZero(double value) => value == 0d ? 0d : value;

        private static double RequireFinite(double value, string context)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArenaContentExportException(
                    $"Non-finite authored value ({value}) at {context}: NaN and infinity cannot be quantised " +
                    "into a reproducible hash and are a build-time export failure (MultiplayerLoadingContract §5.2.1).");
            }

            return value;
        }
    }
}
