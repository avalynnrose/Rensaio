using Microsoft.EntityFrameworkCore;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils.ImageHashing;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// "Smart Search" for the Contribution page (partial of <see cref="ContributionMappingService"/>).
///
/// Unlike the normal per-provider search (which queries the provider with ONE title typed by the
/// user), Smart Search ignores any query text: it searches the target provider with EVERY title of
/// the contribution mapping, scores each returned candidate with the SAME title + image (PDQ)
/// algorithm the metadata matcher uses (<see cref="TitleMatcher"/> + <see cref="ImageMatchCore"/>),
/// and returns only the best-ranked candidates.
///
/// Reference images are the ones already known for the mapping: the member source covers
/// (<c>Data.ThumbnailUrl</c>) and the covers of the providers ALREADY matched to this mapping
/// (<c>ContributionMetadataEntity.CoverUrl</c>). Every candidate cover is compared against ALL of
/// them and the closest comparison is used as its image signal.
/// </summary>
public sealed partial class ContributionMappingService
{
    /// <summary>Number of ranked candidates Smart Search returns.</summary>
    public const int SmartSearchTopN = 10;

    /// <summary>
    /// Weight (0..1) the image signal contributes to the blended score; the remainder is title.
    /// Title stays dominant — a cover match can reorder near-ties but never lets a poorly-title-
    /// matched series outrank a strong title match by image alone (exact-same-cover aside).
    /// </summary>
    private const double SmartSearchImageWeight = 0.25;

    /// <summary>Score floor granted when a candidate cover is the SAME image as a reference cover.</summary>
    private const double SmartSearchSameImageFloor = 0.95;

    /// <summary>
    /// Runs Smart Search for one (mapping, provider) pair. Returns the top
    /// <see cref="SmartSearchTopN"/> candidates ranked by the blended title+image score.
    /// Best-effort: provider/image failures degrade to a lower (or title-only) score.
    /// </summary>
    public async Task<SmartSearchResultDto> SmartSearchAsync(
        Guid mappingId, ExternalSeriesProvider provider, List<IExternalSeriesProvider> providers,
        CancellationToken token = default)
    {
        var result = new SmartSearchResultDto { Provider = provider };

        // 1. Every title of the mapping (the automerge identity set) — same source the scan uses.
        var seriesTitles = await _contributorDb.SeriesTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.SeriesId == mappingId
                && mt.Version != VersionLogicalDelete
                && mt.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var titleCandidates = seriesTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .Select(mt => mt.Title!.Title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titleCandidates.Count == 0) return result;

        // 2. The provider instance to search (prefer the caller's instantiated/authenticated one).
        var scrobbler = providers.FirstOrDefault(p => p.ProviderType == provider)
            ?? _providerFactory.GetProvider(provider);
        if (scrobbler == null) return result;

        // 3. Reference image set: series source covers + covers of the providers already matched.
        var referenceUrls = await CollectReferenceCoverUrlsAsync(mappingId, token).ConfigureAwait(false);
        var referenceHashes = referenceUrls.Count > 0
            ? await _imageMatchCore.GetProviderHashesAsync(referenceUrls, token).ConfigureAwait(false)
            : new Dictionary<string, PdqHash256>(StringComparer.OrdinalIgnoreCase);

        // 4. Search the provider with EVERY distinct title (near-duplicate collapse + provider filter).
        var seenSearchKeys = new HashSet<string>(StringComparer.Ordinal);
        var searchTitles = scrobbler.FilterLookupTitles(titleCandidates)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => seenSearchKeys.Add(SmartSearchTitleKey(t)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byId = new Dictionary<string, ScrobblerSearchResult>(StringComparer.OrdinalIgnoreCase);
        var searchCandidates = new List<(string SearchTitle, string Id)>();

        foreach (var title in searchTitles)
        {
            IReadOnlyList<ScrobblerSearchResult> results;
            try
            {
                results = await scrobbler.SearchSeriesAsync(title, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue; // provider failure on one title — keep the others
            }

            foreach (var r in results)
            {
                if (string.IsNullOrWhiteSpace(r.ExternalId) || string.IsNullOrWhiteSpace(r.Title)) continue;
                if (byId.ContainsKey(r.ExternalId)) continue;
                byId[r.ExternalId] = r;
                searchCandidates.Add((r.Title, r.ExternalId));
                foreach (var alt in r.AlternateTitles.Where(a => !string.IsNullOrWhiteSpace(a)))
                    searchCandidates.Add((alt, r.ExternalId));
            }
        }

        if (byId.Count == 0) return result;

        // 5. Title scoring: best percentage per candidate id against every mapping title.
        var titleScoreById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (searchCandidates.Count > 0)
        {
            var scored = TitleMatcher.MatchTitles(titleCandidates, searchCandidates, 0);
            foreach (var group in scored.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase))
                titleScoreById[group.Key] = group.Max(x => x.Percentage);
        }

        // 6. Image scoring: hash each candidate cover once, then take the closest comparison to
        //    ANY reference cover (the mapping may have several source/provider covers).
        var imageByCover = new Dictionary<string, (int Distance, int Percentage)>(StringComparer.OrdinalIgnoreCase);
        if (referenceHashes.Count > 0)
        {
            var candidateCoverUrls = byId.Values
                .Select(r => r.CoverUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateHashes = candidateCoverUrls.Count > 0
                ? await _imageMatchCore.GetProviderHashesAsync(candidateCoverUrls, token).ConfigureAwait(false)
                : new Dictionary<string, PdqHash256>(StringComparer.OrdinalIgnoreCase);

            foreach (var (coverUrl, candidateHash) in candidateHashes)
            {
                if (candidateHash.IsEmpty) continue;
                var bestDistance = int.MaxValue;
                foreach (var referenceHash in referenceHashes.Values)
                {
                    if (referenceHash.IsEmpty) continue;
                    var distance = PdqHashUtility.Compare(in candidateHash, in referenceHash);
                    if (distance < bestDistance) bestDistance = distance;
                }
                if (bestDistance != int.MaxValue)
                    imageByCover[coverUrl] = (bestDistance, ImageMatchScoring.DistanceToPercentage(bestDistance));
            }
        }

        // 7. Blend + rank + top N.
        var hits = new List<SmartSearchHitDto>(byId.Count);
        foreach (var r in byId.Values)
        {
            var titleScore = titleScoreById.GetValueOrDefault(r.ExternalId, 0);
            int? imageDistance = null;
            int? imageScore = null;
            if (!string.IsNullOrWhiteSpace(r.CoverUrl)
                && imageByCover.TryGetValue(r.CoverUrl!, out var img))
            {
                imageDistance = img.Distance;
                imageScore = img.Percentage;
            }

            double score = titleScore;
            if (imageScore is { } imgPct)
            {
                score = (1.0 - SmartSearchImageWeight) * titleScore + SmartSearchImageWeight * imgPct;
                // A cover that is the SAME image as a reference is a strong signal (mirrors the
                // matcher's same-image promotion): floor its score near the top.
                if (imageDistance is { } d && ImageMatchScoring.IsSameImage(d))
                    score = Math.Max(score, SmartSearchSameImageFloor * 100.0);
            }

            hits.Add(new SmartSearchHitDto
            {
                ExternalId = r.ExternalId,
                Title = r.Title,
                AlternateTitles = r.AlternateTitles ?? [],
                CoverUrl = r.CoverUrl,
                Type = r.Type,
                Year = r.Year,
                Score = Math.Round(score, 2),
                TitleScore = titleScore,
                ImageScore = imageScore,
                ImageDistance = imageDistance
            });
        }

        result.Results = hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.TitleScore)
            .ThenBy(h => h.Title)
            .Take(SmartSearchTopN)
            .ToList();

        _logger.LogInformation(
            "Smart Search {Provider} for mapping {MappingId}: {Candidates} candidate(s) from {Titles} title(s), {Refs} reference cover(s); returning {Returned}",
            provider, mappingId, byId.Count, searchTitles.Count, referenceHashes.Count, result.Results.Count);

        return result;
    }

    /// <summary>
    /// Distinct cover URLs already known for the mapping: the member source covers
    /// (<c>Data.ThumbnailUrl</c>) and the covers of the providers already matched to it.
    /// </summary>
    private async Task<List<string>> CollectReferenceCoverUrlsAsync(Guid mappingId, CancellationToken token)
    {
        var urls = new List<string>();

        var entries = await _contributorDb.SeriesEntries
            .Where(se => se.SeriesId == mappingId
                && se.Version != VersionLogicalDelete
                && se.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var e in entries)
        {
            var u = e.Data?.ThumbnailUrl;
            if (!string.IsNullOrWhiteSpace(u)) urls.Add(u!);
        }

        var metadataRows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var m in metadataRows)
            if (!string.IsNullOrWhiteSpace(m.CoverUrl)) urls.Add(m.CoverUrl!);

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Collapses near-duplicate title spellings for SEARCH purposes only: strips a trailing
    /// parenthetical language/qualifier, folds quote/punctuation variants, lower-cases. Mirrors the
    /// metadata matcher's collapse so the provider is queried once per distinct title.
    /// </summary>
    private static string SmartSearchTitleKey(string title)
    {
        var t = title.Trim();
        if (t.Length > 0 && t[^1] == ')')
        {
            int open = t.LastIndexOf('(');
            if (open > 0)
            {
                var inner = t[(open + 1)..^1];
                if (inner.Length > 0 && inner.Length <= 20
                    && inner.All(c => char.IsLetter(c) || c == ' ' || c == '-' || c == '_'))
                {
                    t = t[..open].Trim();
                }
            }
        }

        var sb = new System.Text.StringBuilder(t.Length);
        foreach (var ch in t.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
