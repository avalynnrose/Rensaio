using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace RensaioBackend.Models.Database
{
    /// <summary>
    /// Persistent PDQ hash cache keyed by cover image URL. Lets the image matcher reuse a
    /// previously computed perceptual hash instead of re-downloading and re-hashing the same
    /// cover on every metadata pass.
    /// </summary>
    [Index(nameof(Url))]
    public class ImageHashCacheEntity
    {
        [Key]
        public string Url { get; set; } = "";

        /// <summary>64-character hexadecimal PDQ hash.</summary>
        public string Hash { get; set; } = "";

        /// <summary>PDQ quality metric (0..100) of the hashed image.</summary>
        public int Quality { get; set; }

        /// <summary>When the hash was computed (UTC).</summary>
        public DateTime UpdatedUTC { get; set; }
    }
}
