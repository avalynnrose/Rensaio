-- 0012_series_verified_date.sql
-- Rensaio Contribution Database — carry the per-series-entry VerifiedDateUTC flag.
--
-- VerifiedDateUTC records the UTC timestamp at which a source entry was manually verified
-- on the Contribution page (NULL = unverified). The worker only TRANSFERS this value: it is
-- persisted on upload and re-emitted in the export, never queried or filtered here, so no
-- index is added (the local contributor.db carries the query indexes).
--
-- One-way toward the cloud: a non-null value propagates from a client; a local clear
-- (Un-Verify / Join / Split) is local-only and is never uploaded, so a verified series on the
-- cloud cannot be flipped to unverified by a plain sweep.

ALTER TABLE series ADD COLUMN verified_date_utc TEXT;
