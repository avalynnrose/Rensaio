-- 0011_add_source_latest_ids.sql
-- Rensaio Contribution Database — add the per-source LatestIds sweep cursor.
--
-- Stores the ordered (most-recent first) series-entry ids of the last GetLatest run
-- for a source, as a JSON array in TEXT (mirrors how the backend serializes it).
-- The backend's incremental sweep pages GetLatest until its ids re-overlap this set,
-- avoiding a full GetPopular pass. Replicated alongside the source.

ALTER TABLE sources ADD COLUMN latest_ids TEXT;
