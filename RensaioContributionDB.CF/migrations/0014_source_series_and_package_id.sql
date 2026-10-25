-- 0014_source_series_and_package_id.sql
-- Rensaio Contribution Database — package-scoped series identity + many-to-many sources.
--
-- Series identity is now package:url (see the backend's ContributionSeriesEntity.DeriveId), so a
-- series row is no longer owned by a single source. `series.source_id` is retained as a
-- representative/legacy FK, while the authoritative source set lives in the `source_series`
-- association table (one row per source that contributes the series).
--
-- package_id = MD5("package") as text Guid, mirroring the local contributor.db. Added to both
-- `sources` and `series` so a series entry and every contributing source share one package scope.
-- Nullable: pre-existing rows are backfilled by the client on the next upload/export.

ALTER TABLE sources ADD COLUMN package_id TEXT;
ALTER TABLE series ADD COLUMN package_id TEXT;

CREATE TABLE source_series (
  source_id            TEXT NOT NULL,
  series_entity_id     TEXT NOT NULL,
  replication_version  INTEGER,
  archived_at          TEXT,
  PRIMARY KEY (source_id, series_entity_id),
  FOREIGN KEY (source_id) REFERENCES sources(id),
  FOREIGN KEY (series_entity_id) REFERENCES series(id)
);

CREATE INDEX idx_source_series_series_entity_id ON source_series(series_entity_id);
CREATE INDEX idx_source_series_archived ON source_series(archived_at);
