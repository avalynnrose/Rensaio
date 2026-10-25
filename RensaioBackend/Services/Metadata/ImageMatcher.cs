using RensaioBackend.Utils.ImageHashing;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Image matching engine: scores candidate provider covers against a local series cover using
/// PDQ perceptual hashing. Mirrors the role of
/// <see cref="RensaioBackend.Services.Scrobbling.TitleMatcher"/> for titles: given a local hash
/// and a set of candidate <c>(Id, Hash)</c> pairs, returns the best matches sorted by ascending
/// Hamming distance (i.e. best similarity first).
/// </summary>
public class ImageMatcher
{
    /// <summary>
    /// Scores every candidate hash against the local hash. Candidates without a usable hash are
    /// skipped. Results are ordered by ascending distance, then by id.
    /// </summary>
    public static (TId Id, int Distance, int Percentage)[] MatchImages<TId>(
        PdqHash256 localHash,
        IEnumerable<(TId Id, PdqHash256 Hash)> candidates)
    {
        if (localHash.IsEmpty)
            return Array.Empty<(TId Id, int Distance, int Percentage)>();

        return candidates
            .Where(c => !c.Hash.IsEmpty)
            .Select(c =>
            {
                int distance = PdqHashUtility.Compare(in localHash, in c.Hash);
                return (c.Id, distance, ImageMatchScoring.DistanceToPercentage(distance));
            })
            .OrderBy(x => x.distance)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    /// <summary>
    /// Convenience helper for a single comparison. Returns false when either hash is empty.
    /// </summary>
    public static bool TryCompare(PdqHash256 localHash, PdqHash256 candidateHash, out int distance, out int percentage)
    {
        distance = 0;
        percentage = 0;
        if (localHash.IsEmpty || candidateHash.IsEmpty)
            return false;
        distance = PdqHashUtility.Compare(in localHash, in candidateHash);
        percentage = ImageMatchScoring.DistanceToPercentage(distance);
        return true;
    }
}
