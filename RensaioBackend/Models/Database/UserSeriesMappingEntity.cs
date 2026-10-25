namespace RensaioBackend.Models.Database;

/// <summary>
/// Shared mapping/decision state. Mappings are global-only (the per-user
/// <c>UserSeriesMappings</c> table has been dropped in favor of the global
/// <see cref="SeriesMappingEntity"/>); old integer values are kept stable so persisted data is
/// never renumbered. <see cref="Blocked"/> means the specific (series/provider) pair must never be
/// auto-linked; <see cref="ManualIgnore"/> is a deliberate manual refusal with no expiration
/// (LinkedDate records when it was triggered); <see cref="ForeverIgnored"/> is never auto-linked
/// again.
/// </summary>
public enum SeriesMappingStatus
{
    Unmatched = 0,
    AutoMatched = 1,
    UserConfirmed = 2,
    ManualIgnore = 3,
    ForeverIgnored = 4,
    Blocked = 5
}