-- 0013_series_metadata_flag.sql
-- Rensaio Contribution Database — carry the per-series-entry Metadata flag.
--
-- Metadata is a DERIVED flag: true when the owning series has at least one LIVE metadata row
-- (a real provider link or an empty-key Unmatched placeholder), false when it has none. It
-- records whether a metadata scan has already been attempted for the series. It is NEVER
-- authoritative: it can be recomputed from the series → metadata relationship at any time.
--
-- IMPORTANT: true does NOT mean "matched" — that still requires an AutoMatched/UserConfirmed
-- mapping_status. Consumers gating on an active link must NOT rely on this flag.
--
-- The worker only TRANSFERS this value: it is persisted on upload and re-emitted in the export,
-- never queried or filtered here, so no index is added (the local contributor.db carries the
-- query index). NOT NULL DEFAULT 0 so pre-existing rows materialize as false (safe: "still
-- needs a metadata pass").

ALTER TABLE series ADD COLUMN metadata_flag INTEGER NOT NULL DEFAULT 0;
