using System.Text.RegularExpressions;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Scrobbling;

/// <summary>
/// Recognizes the two "direct reference" forms the Search requester accepts (in addition to a
/// normal free-text query):
///   1. <c>#{id}</c> — an explicit external id for the CURRENT provider (no search).
///   2. A provider series URL (e.g. <c>https://anilist.co/manga/103094</c>) — the external id is
///      deducted from the provider's own <see cref="IExternalSeriesProvider.SeriesUrlTemplate"/>.
///
/// In both cases the caller should resolve the id via a DETAIL fetch (not a search) and surface the
/// single resulting candidate to the user — nothing is confirmed automatically.
/// </summary>
public static class DirectSeriesReference
{
    /// <summary>
    /// Attempts to extract a provider external id from <paramref name="query"/>. Returns true and
    /// sets <paramref name="externalId"/> when the input is a recognized direct reference for
    /// <paramref name="provider"/>; otherwise returns false (the caller should run a normal search).
    /// </summary>
    public static bool TryParse(IExternalSeriesProvider provider, string? query, out string externalId)
    {
        externalId = string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return false;
        var q = query.Trim();

        // (1) Explicit id for THIS provider: "#<id>".
        if (q.StartsWith('#'))
        {
            var id = q[1..].Trim();
            if (id.Length == 0) return false;
            externalId = id;
            return true;
        }

        // (2) Provider URL — deduct the id from the provider's SeriesUrlTemplate.
        var template = provider.SeriesUrlTemplate;
        if (string.IsNullOrWhiteSpace(template)) return false;
        var placeholder = template.IndexOf("{0}", StringComparison.Ordinal);
        if (placeholder < 0) return false;

        // Only treat the input as a URL when it carries a host (a dotted segment or a scheme).
        var url = q;
        if (!url.Contains("://", StringComparison.Ordinal))
        {
            if (!url.Contains('.')) return false;
            url = "https://" + url;
        }

        // Normalize away a leading "www." and a trailing slash so the template's own host/suffix
        // match regardless of cosmetic differences.
        url = Normalize(url).TrimEnd('/');
        var prefix = Normalize(template[..placeholder]);
        var suffix = Normalize(template[(placeholder + 3)..]).TrimEnd('/');

        // The id is the FIRST path segment after the prefix, so extra trailing segments
        // (mangaupdates '/series/{id}/slug', mangabaka '/manga/{id}/slug') are ignored.
        var pattern = "^" + Regex.Escape(prefix) + "(?<id>[^/?#]+)" + Regex.Escape(suffix) + ".*$";
        var match = Regex.Match(url, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return false;

        var extracted = Uri.UnescapeDataString(match.Groups["id"].Value).Trim();
        if (extracted.Length == 0) return false;
        externalId = extracted;
        return true;
    }

    private static string Normalize(string value)
        => value.Replace("://www.", "://", StringComparison.OrdinalIgnoreCase);
}
