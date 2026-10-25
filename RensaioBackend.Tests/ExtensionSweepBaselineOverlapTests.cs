using RensaioBackend.Services.Background;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Focused tests for the incremental GetLatest baseline detection used by
/// <see cref="ExtensionSweepService"/>. Regression guard for the bug where the sweep never
/// re-detected the stored baseline and always fell back to a full GetPopular pass.
///
/// GetLatest is newest-first, so the stored baseline reappears in a later run shifted DOWN by the
/// items published in between. The matcher must leave those leading new items free (that is the
/// whole point of the incremental pass) and only tolerate a few interior gaps.
/// </summary>
public sealed class ExtensionSweepBaselineOverlapTests
{
    private const int Threshold = 7;
    private const int MaxMisses = 3;

    private static int Matched(string[] current, string[] stored) =>
        ExtensionSweepService.CountBaselineMatches(current, stored, MaxMisses);

    [Fact]
    public void IdenticalSequence_MatchesEverything()
    {
        string[] ids = ["a", "b", "c", "d", "e", "f", "g", "h"];
        Assert.Equal(ids.Length, Matched(ids, ids));
        Assert.True(Matched(ids, ids) >= Threshold);
    }

    [Fact]
    public void ManyLeadingNewItems_DoNotBlockTheBaseline()
    {
        // Ten brand-new entries were published since the last run; the old baseline (a..j) follows.
        // The leading items must be free, so the full baseline still re-matches.
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];
        string[] current =
        [
            "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10",
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j"
        ];

        Assert.Equal(stored.Length, Matched(current, stored));
        Assert.True(Matched(current, stored) >= Threshold, "10 leading new items must not force a fallback");
    }

    [Fact]
    public void FewerThanThresholdMatches_ReportsBelowThreshold()
    {
        // Only 3 of the baseline re-appeared before the tail ran out -> not enough to stop.
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h"];
        string[] current = ["n1", "n2", "a", "b", "c"];

        Assert.Equal(3, Matched(current, stored));
        Assert.True(Matched(current, stored) < Threshold);
    }

    [Fact]
    public void UpToMaxInteriorGaps_AreAbsorbed()
    {
        // Baseline a..h with three interior gaps (x) between matches -> still counted in full.
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h"];
        string[] current = ["a", "x1", "b", "x2", "c", "x3", "d", "e", "f", "g", "h"];

        Assert.Equal(stored.Length, Matched(current, stored));
    }

    [Fact]
    public void MoreThanMaxInteriorGaps_StopsCounting()
    {
        // Four interior gaps in a row exceeds the tolerance: the walk stops at the divergence.
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h"];
        string[] current = ["a", "x1", "x2", "x3", "x4", "b", "c", "d", "e", "f", "g", "h"];

        Assert.Equal(1, Matched(current, stored));
    }

    [Fact]
    public void WindowScrolledPast_DoesNotMatch()
    {
        // The run moved far past the stored baseline (a single early id, then all-new content).
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h"];
        string[] current = ["n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8", "n9", "n10"];

        Assert.Equal(0, Matched(current, stored));
        Assert.True(Matched(current, stored) < Threshold);
    }

    [Fact]
    public void EmptyInputs_ReturnZero()
    {
        Assert.Equal(0, Matched([], ["a", "b", "c"]));
        Assert.Equal(0, Matched(["a", "b", "c"], []));
        Assert.Equal(0, Matched([], []));
    }

    [Fact]
    public void RepeatedIdsInCurrent_DoNotAdvanceTheBaselinePointer()
    {
        // A republished entry (same id twice) must not be consumed as two baseline matches.
        string[] stored = ["a", "b", "c", "d", "e", "f", "g", "h"];
        string[] current = ["a", "a", "b", "c"];

        Assert.Equal(3, Matched(current, stored));
    }
}
