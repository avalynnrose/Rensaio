-- ============================================================================
-- purge-package.sql
-- ----------------------------------------------------------------------------
-- Hard-delete, from the LOCAL contribution database (`contributor.db`, the
-- EF/SQLite store managed by RensaioBackend, see
-- RensaioBackend/Data/ContributionDbContext.cs), every row that belongs
-- EXCLUSIVELY to one extension package.
--
-- Rows SHARED with another package are preserved.
--
-- USAGE
--   1. Replace the placeholder package id below (the literal
--      'com.example.myextension') with the package you want to purge. Replace
--      EVERY occurrence (it is referenced in several statements).
--   2. Close Rensaio (so the app is not holding the DB open / writing), then run
--      the WHOLE file with the sqlite3 CLI, e.g.:
--        sqlite3 "<path>\appdata\local\rensaio\contributor.db" ".read ./purge-package.sql"
--      (On Windows you can also paste it into any SQLite client, e.g. DB Browser
--       for SQLite -> Execute SQL.)
--   3. The script is idempotent: running it again is a safe no-op.
--
-- TABLE / COLUMN MAP (local `contributor.db`)
--   Sources       (Id BLOB16 PK, Package, SourceId, SourceName, SourceLanguage,
--                  LastBatchExecutionUTC, LatestIds, Version)
--   SeriesEntries (Id BLOB16 PK, series_id BLOB16 -> Series.Id,
--                  SourceId BLOB16 -> Sources.Id, Data TEXT = ContributionRecordV2 JSON,
--                  VerifiedDateUTC, Version)
--   Series        (Id BLOB16 PK)                      <-- the mapping ("series") aggregate
--   SeriesTitles  (series_id BLOB16, title_id BLOB16, version)  <-- title links
--   Titles        (Id BLOB16 PK, Title, Version)
--   Metadata      (Id BLOB16 PK, series_id BLOB16 -> Series.Id, ProviderId,
--                  ProviderKey, MappingStatus, cover_url, provider_title_ids, Version)
--
-- VERSION SEMANTICS (RensaioBackend/Services/Contributions/ContributionVersions.cs)
--   A row is LIVE when  Version NOT IN (-1, -3):
--      0  AddOrUpdate (pending)     -2 Uploaded          -> LIVE
--     -1  PendingDelete (tombstone) -3 UploadedDelete    -> deleted
--
-- BLOB / JSON GUID ENCODING (RensaioBackend/Data/Converters/GuidToBlob16Converter.cs)
--   Every Id/FK column is a 16-byte BLOB holding the Guid's canonical 32-hex-char
--   form IN ORDER — so `lower(hex(Id))` is exactly the Guid string without dashes.
--   `SeriesEntries.Data` is a ContributionRecordV2 JSON whose `"i"` field is the
--   title Guid (System.Text.Json emits it lowercase, dashed). Normalizing BOTH
--   sides to a lowercase dash-less hex string makes them directly comparable:
--       lower(replace(json_extract(Data, '$.i'), '-', ''))  ==  lower(hex(Id))
--
-- PERFORMANCE
--   The naive form (`hex(Titles.Id) = lower(replace(json_extract(Data,...)))`)
--   inside a JOIN is an UNINDEXABLE nested loop (Titles x SeriesEntries), and
--   re-evaluating the same expression per candidate title is quadratic —
--   effectively "forever" on a large graph. This script instead:
--     * normalizes each entry's title GUID exactly ONCE, into a staging column,
--     * drives every step from an INDEX (Sources.Package, Sources.Id,
--       SeriesEntries.SourceId / series_id, SeriesTitles.title_id / series_id,
--       Metadata.series_id, and the staged title-hex tables),
--     * compares plain strings/columns (no hex<->blob decoding), and
--     * does each "still referenced?" test as an index seek, never a per-row
--       rescan of SeriesEntries.
--   No `unhex()` is used, so this runs on any SQLite build with JSON1 (which
--   `json_extract` already proves is present).
--
-- OWNERSHIP MODEL (why each table is treated the way it is)
--   * packages        -> the ONLY package identity lives on `Sources.Package`.
--   * Sources         -> a source's identity (MD5("package:sourceId")) is
--                        package-scoped, so every source of the package goes.
--   * SeriesEntries   -> each entry references the source it was derived from, so
--                        all entries of the package go.
--   * Metadata / SeriesTitles -> carry no package; they are owned by their Series.
--                        Removed only when that Series is owned solely by this
--                        package.
--   * Series          -> a "series" (mapping) is removed only when EVERY LIVE
--                        entry pointing at it comes from this package. A series
--                        shared with another package survives, as do its
--                        Metadata / SeriesTitles rows.
--   * Titles          -> the shared title registry. A title is removed only when,
--                        AFTER the deletions above, nothing references it any more
--                        (no LIVE SeriesTitles row and no residual SeriesEntries
--                        title id). Titles still referenced by another package are
--                        kept.
--
-- FK SAFETY: statements run child-first, so foreign keys are never violated.
-- `PRAGMA defer_foreign_keys` postpones enforcement to COMMIT as a safety net.
--
-- IMPORTANT — CLOUD PROPAGATION (read before running)
--   This is a LOCAL purge. A hard DELETE is not a replication tombstone, so the
--   removal is NOT queued for upload: the cloud worker keeps the package's rows
--   and any already-queued local delete (-1) for these rows is discarded. If the
--   goal is to remove the package from the CLOUD too, do NOT hard-delete the
--   cloud-backed rows; instead tombstone them (set Version = -1) so the next
--   contribution upload propagates the delete. See the OPTIONAL section at the
--   bottom for that variant.
-- ============================================================================

-- One transaction: avoids per-statement fsync and makes the purge all-or-nothing.
BEGIN IMMEDIATE;

PRAGMA defer_foreign_keys = TRUE;

-- ---------------------------------------------------------------------------
-- 0. Staging tables (real tables so the script also works when a runner issues
--    statements one at a time). Cleared + re-indexed to stay re-runnable.
--    Note: title identities are staged as LOWERCASE HEX TEXT, not BLOBs.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS zz_scrub_sources     (id BLOB PRIMARY KEY NOT NULL);
CREATE TABLE IF NOT EXISTS zz_scrub_mappings    (id BLOB PRIMARY KEY NOT NULL);
CREATE TABLE IF NOT EXISTS zz_scrub_titles      (title_hex TEXT PRIMARY KEY NOT NULL);
CREATE TABLE IF NOT EXISTS zz_scrub_ref_titles  (title_hex TEXT NOT NULL);

DELETE FROM zz_scrub_sources;
DELETE FROM zz_scrub_mappings;
DELETE FROM zz_scrub_titles;
DELETE FROM zz_scrub_ref_titles;

-- ---------------------------------------------------------------------------
-- 1. The package's sources. Index seek on IX_Sources_Package_SourceId.
-- ---------------------------------------------------------------------------
INSERT INTO zz_scrub_sources (id)
SELECT Id FROM Sources WHERE Package = 'com.example.myextension';

-- ---------------------------------------------------------------------------
-- 2. The series (mappings) owned EXCLUSIVELY by the package.
--      (a) seed with every series that has an entry from a package source —
--          an index seek on SeriesEntries.SourceId (bounded to the package).
--      (b) drop any series that ALSO has a LIVE entry from a different package —
--          an EXISTS probe, one index seek per candidate series (few).
--    This replaces a full `GROUP BY series_id` scan of SeriesEntries.
-- ---------------------------------------------------------------------------
INSERT INTO zz_scrub_mappings (id)
SELECT DISTINCT se.series_id
FROM SeriesEntries se
WHERE se.SourceId IN (SELECT id FROM zz_scrub_sources);

DELETE FROM zz_scrub_mappings
WHERE EXISTS (
        SELECT 1
        FROM SeriesEntries se
        WHERE se.series_id = zz_scrub_mappings.id
          AND se.Version NOT IN (-1, -3)
          AND se.SourceId NOT IN (SELECT id FROM zz_scrub_sources));

-- ---------------------------------------------------------------------------
-- 3. CANDIDATE titles of the package's data (staged as lowercase hex):
--      (a) titles referenced by an entry of the package. Bounded to the
--          package's entries by the SourceId index; the GUID is normalized ONCE.
--      (b) titles linked (LIVE SeriesTitles) to a scope series — title_id index.
-- ---------------------------------------------------------------------------
INSERT OR IGNORE INTO zz_scrub_titles (title_hex)
SELECT DISTINCT lower(replace(json_extract(se.Data, '$.i'), '-', ''))
FROM SeriesEntries se
WHERE se.SourceId IN (SELECT id FROM zz_scrub_sources)
  AND json_extract(se.Data, '$.i') IS NOT NULL;

INSERT OR IGNORE INTO zz_scrub_titles (title_hex)
SELECT DISTINCT lower(hex(st.title_id))
FROM SeriesTitles st
WHERE st.version NOT IN (-1, -3)
  AND st.series_id IN (SELECT id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 3.5 Title ids still referenced by entries OUTSIDE the package. Computed in a
--     SINGLE pass (each residual entry's title GUID normalized once), then
--     indexed so step 7's "is this title still referenced?" test is a binary
--     seek — never a per-title rescan of SeriesEntries.
-- ---------------------------------------------------------------------------
INSERT INTO zz_scrub_ref_titles (title_hex)
SELECT lower(replace(json_extract(se.Data, '$.i'), '-', ''))
FROM SeriesEntries se
WHERE se.SourceId NOT IN (SELECT id FROM zz_scrub_sources)
  AND json_extract(se.Data, '$.i') IS NOT NULL;

CREATE INDEX IF NOT EXISTS zz_scrub_ref_titles_idx ON zz_scrub_ref_titles(title_hex);

-- ---------------------------------------------------------------------------
-- 4. Delete the scope series' children FIRST (child -> parent order). A series
--    shared with another package is NOT in zz_scrub_mappings, so its Metadata /
--    SeriesTitles rows are left untouched. Both use a series_id index.
-- ---------------------------------------------------------------------------
DELETE FROM SeriesTitles
WHERE series_id IN (SELECT id FROM zz_scrub_mappings);

DELETE FROM Metadata
WHERE series_id IN (SELECT id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 5. Delete the package's entries (children of Sources, via the SourceId index),
--    then the package's Sources (via the Package index).
-- ---------------------------------------------------------------------------
DELETE FROM SeriesEntries
WHERE SourceId IN (SELECT id FROM zz_scrub_sources);

DELETE FROM Sources
WHERE Package = 'com.example.myextension';

-- ---------------------------------------------------------------------------
-- 6. Delete the now-childless series that were owned solely by the package.
--    (Their SeriesEntries / Metadata / SeriesTitles rows were removed above.)
-- ---------------------------------------------------------------------------
DELETE FROM Series
WHERE Id IN (SELECT id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 7. Delete the candidate titles that are now completely unreferenced.
--    One scan of Titles (cheap: hex() per row + a staged-table index seek),
--    then two index seeks per row:
--      * no LIVE SeriesTitles row        -> SeriesTitles.title_id index,
--      * no residual entry references it -> zz_scrub_ref_titles binary seek.
--    A title still used by another package therefore survives.
-- ---------------------------------------------------------------------------
DELETE FROM Titles
WHERE lower(hex(Titles.Id)) IN (SELECT title_hex FROM zz_scrub_titles)
  AND NOT EXISTS (
        SELECT 1 FROM SeriesTitles st
        WHERE st.title_id = Titles.Id
          AND st.version NOT IN (-1, -3))
  AND NOT EXISTS (
        SELECT 1 FROM zz_scrub_ref_titles r
        WHERE r.title_hex = lower(hex(Titles.Id)));

-- ---------------------------------------------------------------------------
-- 8. Drop the staging tables (the helper index goes with its table).
-- ---------------------------------------------------------------------------
DROP TABLE zz_scrub_sources;
DROP TABLE zz_scrub_mappings;
DROP TABLE zz_scrub_titles;
DROP TABLE zz_scrub_ref_titles;

COMMIT;

-- ============================================================================
-- VERIFICATION — every count below must be 0 after the purge.
-- ============================================================================
SELECT 'sources' AS what, COUNT(*) AS remaining
FROM Sources WHERE Package = 'com.example.myextension'

UNION ALL
SELECT 'series_entries', COUNT(*)
FROM SeriesEntries se
WHERE se.SourceId IN (SELECT Id FROM Sources WHERE Package = 'com.example.myextension')

UNION ALL
SELECT 'series_owned_only_by_package', COUNT(*)
FROM Series s
WHERE EXISTS (
        SELECT 1 FROM SeriesEntries se
        WHERE se.series_id = s.Id AND se.Version NOT IN (-1, -3)
          AND se.SourceId IN (SELECT Id FROM Sources WHERE Package = 'com.example.myextension'))
  AND NOT EXISTS (
        SELECT 1 FROM SeriesEntries se
        WHERE se.series_id = s.Id AND se.Version NOT IN (-1, -3)
          AND se.SourceId NOT IN (SELECT Id FROM Sources WHERE Package = 'com.example.myextension'));

-- ============================================================================
-- OPTIONAL — DRY RUN. Replace the delete steps (4-8) + COMMIT with the previews
-- below (still inside the transaction, then ROLLBACK) to see exactly what WOULD
-- be removed, changing nothing.
-- ============================================================================
-- SELECT 'sources_to_delete' AS what, hex(id) AS id FROM zz_scrub_sources;
-- SELECT 'mappings_to_delete' AS what, hex(id) AS id FROM zz_scrub_mappings;
-- SELECT 'titles_to_delete' AS what, title_hex AS id
--   FROM Titles
--   WHERE lower(hex(Titles.Id)) IN (SELECT title_hex FROM zz_scrub_titles)
--     AND NOT EXISTS (SELECT 1 FROM SeriesTitles st
--                     WHERE st.title_id = Titles.Id AND st.version NOT IN (-1, -3))
--     AND NOT EXISTS (SELECT 1 FROM zz_scrub_ref_titles r
--                     WHERE r.title_hex = lower(hex(Titles.Id)));
-- SELECT 'series_entries_to_delete' AS what, hex(Id) AS id
--   FROM SeriesEntries WHERE SourceId IN (SELECT id FROM zz_scrub_sources);
-- SELECT 'metadata_to_delete' AS what, hex(Id) AS id
--   FROM Metadata WHERE series_id IN (SELECT id FROM zz_scrub_mappings);
-- SELECT 'series_titles_to_delete' AS what, hex(series_id) || '|' || hex(title_id) AS id
--   FROM SeriesTitles WHERE series_id IN (SELECT id FROM zz_scrub_mappings);
-- ROLLBACK;

-- ============================================================================
-- OPTIONAL — PROPAGATE TO THE CLOUD INSTEAD OF HARD-DELETING.
-- Instead of steps 4-8, retire the package's rows with a tombstone so the next
-- contribution upload removes them from the cloud too. Mirror the app's own rule
-- (ContributionRowLifecycle): a cloud-backed row (Version = -2) is tombstoned to
-- -1 so its delete propagates; a never-exported row (Version = 0) has no cloud
-- copy and can be deleted outright. Example (entries of the package's sources):
--
--   -- 0 was never exported -> hard delete
--   DELETE FROM SeriesEntries
--   WHERE Version = 0 AND SourceId IN (SELECT id FROM zz_scrub_sources);
--   -- -2 exists in the cloud -> tombstone so the delete is uploaded
--   UPDATE SeriesEntries SET Version = -1
--   WHERE Version = -2 AND SourceId IN (SELECT id FROM zz_scrub_sources);
--   -- ...and the same pattern for Sources / SeriesTitles / Metadata / Titles.
-- ============================================================================
