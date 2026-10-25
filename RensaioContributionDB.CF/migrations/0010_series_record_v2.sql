-- 0010_series_record_v2.sql
-- Rensaio Contribution Database — ContributionRecordV2.
--
-- The per-series record (previously ContributionRecordV1) is flattened into the
-- `series` table. V2 adds:
--   * `url`  — the canonical source url the entry was derived from. It is now part
--              of the record payload, so the record is self-describing.
--   * `memo` — an optional free-form note.
--
-- Existing rows get an empty url (unknown for v1 payloads; the contributing client
-- regenerates them as v2 on its next sweep, tombstoning the legacy entry).

ALTER TABLE series ADD COLUMN url TEXT NOT NULL DEFAULT '';
ALTER TABLE series ADD COLUMN memo TEXT;
