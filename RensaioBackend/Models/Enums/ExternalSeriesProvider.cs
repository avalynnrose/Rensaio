namespace RensaioBackend.Models.Enums;

public enum ExternalSeriesProvider
{
    MyAnimeList = 0,
    AniList = 1,
    ComicVine = 2,
    Kitsu = 3,
    MangaDex = 4,

    // ── Metadata providers ──
    // High, stable values so existing persisted ints in the DB are never renumbered.
    MangaBaka = 10,
    Bangumi = 11,
    MangaUpdates = 12,

    // Reserved for a later phase (enum values kept stable for DB identifiers).
    GrandComicsDatabase = 13,
    Metron = 14
}
