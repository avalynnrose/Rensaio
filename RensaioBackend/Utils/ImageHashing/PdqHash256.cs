using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;


namespace RensaioBackend.Utils.ImageHashing
{
    public readonly struct PdqHash256
    {
        private readonly ulong _w0;
        private readonly ulong _w1;
        private readonly ulong _w2;
        private readonly ulong _w3;

        internal PdqHash256(ulong w0, ulong w1, ulong w2, ulong w3)
        {
            _w0 = w0;
            _w1 = w1;
            _w2 = w2;
            _w3 = w3;
        }

        /// <summary>All-zero hash, used as the "no hash computed" sentinel.</summary>
        public static readonly PdqHash256 Empty = new(0, 0, 0, 0);

        /// <summary>True when every word is zero (i.e. <see cref="Empty"/>).</summary>
        public bool IsEmpty => (_w0 | _w1 | _w2 | _w3) == 0;

        /// <summary>
        /// Returns the standard 64-character PDQ hexadecimal representation.
        /// </summary>
        public override string ToString() => $"{_w3:x16}{_w2:x16}{_w1:x16}{_w0:x16}";

        public static PdqHash256 Parse(string value) => Parse(value.AsSpan());

        /// <summary>
        /// Non-throwing parse. Returns false for null/empty/short/malformed values so callers
        /// reading persisted hashes never crash on legacy or corrupt rows.
        /// </summary>
        public static bool TryParse(string? value, out PdqHash256 hash)
        {
            hash = Empty;
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                return false;
            try
            {
                hash = Parse(value.AsSpan());
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        public static PdqHash256 Parse(ReadOnlySpan<char> value)
        {
            if (value.Length != 64)
                throw new FormatException("A PDQ hash must contain exactly 64 hexadecimal characters.");

            ulong w3 = ParseUInt64(value[..16]);
            ulong w2 = ParseUInt64(value.Slice(16, 16));
            ulong w1 = ParseUInt64(value.Slice(32, 16));
            ulong w0 = ParseUInt64(value.Slice(48, 16));

            return new PdqHash256(w0, w1, w2, w3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ParseUInt64(ReadOnlySpan<char> value)
        {
            return ulong.Parse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns the Hamming distance from 0 to 256.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static int HammingDistance(in PdqHash256 left, in PdqHash256 right)
        {
            return
                BitOperations.PopCount(left._w0 ^ right._w0) +
                BitOperations.PopCount(left._w1 ^ right._w1) +
                BitOperations.PopCount(left._w2 ^ right._w2) +
                BitOperations.PopCount(left._w3 ^ right._w3);
        }

    }
}
