namespace RensaioBackend.Utils.ImageHashing
{
    /// <summary>
    /// Shared constants and conversions for image (PDQ) matching, mirroring the role
    /// <see cref="RensaioBackend.Services.Scrobbling.TitleMatcher"/> plays for titles.
    /// </summary>
    public static class ImageMatchScoring
    {
        /// <summary>
        /// Maximum Hamming distance (out of 256) at which two PDQ hashes are considered the
        /// same image. 31/256 is the conventional PDQ same-image threshold.
        /// </summary>
        public const int SameImageThreshold = 31;

        /// <summary>PDQ hashes are 256 bits wide.</summary>
        public const int HashBits = 256;

        /// <summary>
        /// Maps a Hamming distance (0..256) to a 0..100 similarity percentage where 0 = identical.
        /// </summary>
        public static int DistanceToPercentage(int distance)
        {
            if (distance <= 0) return 100;
            if (distance >= HashBits) return 0;
            return (int)Math.Round((1.0 - (double)distance / HashBits) * 100.0);
        }

        /// <summary>True when the distance is within the same-image threshold.</summary>
        public static bool IsSameImage(int distance) => distance <= SameImageThreshold;
    }
}
