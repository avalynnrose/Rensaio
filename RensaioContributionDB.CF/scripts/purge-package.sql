-- ============================================================================
-- purge-package.sql
-- ----------------------------------------------------------------------------
-- Hard-delete, from the Cloudflare D1 contribution database
-- (`rensaio-contribution-db`, worker RensaioContributionDB.CF), every row that
-- belongs EXCLUSIVELY to one extension package.
--
-- Rows SHARED with another package are preserved.
--
-- USAGE
--   1. Replace the placeholder package id below (the literal
--      'com.example.myextension') with the package you want to purge. Replace
--      EVERY occurrence (the script references it in several statements).
--   2. Run the WHOLE file. Either:
--        npx wrangler d1 execute rensaio-contribution-db --remote ^
--            --file=./scripts/purge-package.sql
--      or paste it into the D1 console (Dashboard -> D1 -> Console).
--   3. The script is idempotent: running it again is safe (no-op once purged).
--
-- OWNERSHIP MODEL (why each table is treated the way it is)
--   * packages  -> the ONLY package identity lives on `sources.package`.
--   * sources   -> a source's identity (MD5("package:sourceId")) is
--                  package-scoped, so every source of the package is removed.
--   * series    -> each row references the source it was derived from, so all
--                  series rows of the package are removed.
--   * metadata  -> carries NO package column; it is owned by its mapping.
--                  Removed only when its mapping is owned solely by this
--                  package.
--   * mapping_titles -> likewise owned by its mapping; removed only together
--                  with an exclusively-owned mapping.
--   * mappings  -> a mapping is removed only when EVERY series row that points
--                  at it comes from this package (no other package keeps it).
--                  A mapping shared with another package survives, as do its
--                  metadata / mapping_titles rows.
--   * titles    -> the shared title registry. A title is removed only when,
--                  AFTER the deletions above, nothing references it any more
--                  (no series row and no mapping_titles row left). Titles still
--                  referenced by another package are kept.
--
-- FK SAFETY: statements are ordered child-first, so the deletes never violate
-- a foreign key. `PRAGMA defer_foreign_keys` is set as an extra safety net for
-- runs that execute as a single batch.
-- ============================================================================

PRAGMA defer_foreign_keys = TRUE;

-- ---------------------------------------------------------------------------
-- 0. Staging tables (kept as real tables, not TEMP, so the script also works
--    when the runner issues each statement over a separate connection).
--    `DELETE FROM` first makes the script re-runnable.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS zz_scrub_mappings (mapping_id TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS zz_scrub_titles   (title_id   TEXT PRIMARY KEY);
DELETE FROM zz_scrub_mappings;
DELETE FROM zz_scrub_titles;

-- ---------------------------------------------------------------------------
-- 1. Resolve the mappings owned EXCLUSIVELY by the target package:
--       - at least one series row whose source belongs to the package, AND
--       - zero series rows whose source belongs to any other package.
-- ---------------------------------------------------------------------------
INSERT INTO zz_scrub_mappings (mapping_id)
SELECT s.mapping_id
FROM series s
JOIN sources so ON so.id = s.source_id
GROUP BY s.mapping_id
HAVING SUM(CASE WHEN so.package =  'com.example.myextension' THEN 1 ELSE 0 END) > 0  -- package owns it
   AND SUM(CASE WHEN so.package <> 'com.example.myextension' THEN 1 ELSE 0 END) = 0; -- no other package does

-- ---------------------------------------------------------------------------
-- 2. Resolve the CANDIDATE titles that belong to the package's data:
--       - titles referenced by a series row of the package, plus
--       - titles referenced by the mapping_titles of the scope mappings.
--    (These are only candidates — each is deleted further down ONLY if no
--     reference survives the series / mapping_titles deletions.)
-- ---------------------------------------------------------------------------
INSERT OR IGNORE INTO zz_scrub_titles (title_id)
SELECT s.title_id
FROM series s
JOIN sources so ON so.id = s.source_id
WHERE so.package = 'com.example.myextension';

INSERT OR IGNORE INTO zz_scrub_titles (title_id)
SELECT mt.title_id
FROM mapping_titles mt
WHERE mt.mapping_id IN (SELECT mapping_id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 3. Delete the package's mappings' children FIRST (child -> parent order).
--    A mapping shared with another package is NOT in zz_scrub_mappings, so its
--    metadata / mapping_titles rows are left untouched.
-- ---------------------------------------------------------------------------
DELETE FROM mapping_titles
WHERE mapping_id IN (SELECT mapping_id FROM zz_scrub_mappings);

DELETE FROM metadata
WHERE mapping_id IN (SELECT mapping_id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 4. Delete the package's series rows (children of `sources`) and then the
--    package's source rows.
-- ---------------------------------------------------------------------------
DELETE FROM series
WHERE source_id IN (
    SELECT id FROM sources WHERE package = 'com.example.myextension'
);

DELETE FROM sources
WHERE package = 'com.example.myextension';

-- ---------------------------------------------------------------------------
-- 5. Delete the now-childless mappings that were owned solely by the package.
--    (Their series / metadata / mapping_titles rows were removed in steps 3-4.)
-- ---------------------------------------------------------------------------
DELETE FROM mappings
WHERE id IN (SELECT mapping_id FROM zz_scrub_mappings);

-- ---------------------------------------------------------------------------
-- 6. Delete the candidate titles that are now completely unreferenced.
--    NOT IN (series) AND NOT IN (mapping_titles) means "no other package (or
--    owner) still references this title", so a shared title is preserved.
-- ---------------------------------------------------------------------------
DELETE FROM titles
WHERE id IN (SELECT title_id FROM zz_scrub_titles)
  AND id NOT IN (SELECT title_id FROM series)
  AND id NOT IN (SELECT title_id FROM mapping_titles);

-- ---------------------------------------------------------------------------
-- 7. Drop the staging tables.
-- ---------------------------------------------------------------------------
DROP TABLE zz_scrub_mappings;
DROP TABLE zz_scrub_titles;

-- ---------------------------------------------------------------------------
-- 8. OPTIONAL — flag the change so the next export re-publishes metadata.bin.
--    A hard DELETE is INVISIBLE to the worker's export gate: the data-derived
--    fallback in `hasPendingChanges` only looks at surviving ACTIVE rows, and
--    nothing here set the in-band `pending_changes` flag. So unless you set the
--    flag (or force an export), the removed rows linger in the already-published
--    metadata.bin until some unrelated upload happens to mark changes pending.
--    Uncomment to propagate immediately, then let the daily cron (06:00 UTC)
--    export — or call POST /admin/export?admin={adminUUID} yourself.
-- ---------------------------------------------------------------------------
-- UPDATE replication SET pending_changes = 1, bumped_at = datetime('now') WHERE id = 1;

-- ============================================================================
-- VERIFICATION — every count below must be 0 after the purge.
-- ============================================================================
SELECT 'sources' AS table_name, COUNT(*) AS remaining
FROM sources
WHERE package = 'com.example.myextension'

UNION ALL
SELECT 'series', COUNT(*)
FROM series s
JOIN sources so ON so.id = s.source_id
WHERE so.package = 'com.example.myextension'

UNION ALL
SELECT 'mappings_owned_only_by_package', COUNT(*)
FROM mappings m
WHERE EXISTS (
        SELECT 1 FROM series s JOIN sources so ON so.id = s.source_id
        WHERE s.mapping_id = m.id AND so.package = 'com.example.myextension')
  AND NOT EXISTS (
        SELECT 1 FROM series s JOIN sources so ON so.id = s.source_id
        WHERE s.mapping_id = m.id AND so.package <> 'com.example.myextension');

-- ============================================================================
-- OPTIONAL — DRY RUN. Comment out steps 3-7 above and run the statements below
-- first to preview exactly what would be removed, without changing anything.
-- ============================================================================
-- SELECT 'mappings_to_delete' AS what, mapping_id AS id FROM zz_scrub_mappings;
-- SELECT 'series_to_delete'   AS what, s.id AS id
--   FROM series s JOIN sources so ON so.id = s.source_id
--   WHERE so.package = 'com.example.myextension';
-- SELECT 'metadata_to_delete' AS what, id AS id FROM metadata
--   WHERE mapping_id IN (SELECT mapping_id FROM zz_scrub_mappings);
-- SELECT 'mapping_titles_to_delete' AS what, mapping_id || '|' || title_id AS id
--   FROM mapping_titles WHERE mapping_id IN (SELECT mapping_id FROM zz_scrub_mappings);
-- SELECT 'sources_to_delete'  AS what, id AS id FROM sources
--   WHERE package = 'com.example.myextension';
-- SELECT 'titles_to_delete'   AS what, id AS id FROM titles
--   WHERE id IN (SELECT title_id FROM zz_scrub_titles)
--     AND id NOT IN (SELECT title_id FROM series)
--     AND id NOT IN (SELECT title_id FROM mapping_titles);
