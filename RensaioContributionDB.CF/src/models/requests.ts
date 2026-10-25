/**
 * ── ContributionSnapshotV1 wire format ────────────────────────────────────────
 * Mirrors RensaioBackend.Services.Contributions.Snapshot.ContributionSnapshotV1
 * and the entity classes in Models/ContributionDatabase. Each entity carries a
 * `v` (replication version) field: 0 = add/update (upsert), -1 = delete.
 *
 * The top-level `v` is the collection Replication Version Number the client
 * observed (used for context; the server merges blindly).
 *
 * Guids are serialized as their canonical lowercase-hex text form.
 */

/** TitleEntity — canonical title; id = MD5(normalized title). */
export interface TitleEntityPayload {
  i: string;           // Guid (MD5 of normalized title)
  t: string;           // title text
  v: number;           // 0 | -1
}

/** MappingTitleEntity — join mapping ↔ title. */
export interface MappingTitleEntityPayload {
  m: string;           // MappingId Guid
  t: string;           // TitleId Guid
  v: number;           // 0 | -1
}

/** ContributionRecordV2 — embedded in ContributionSeriesEntity.data (current schema). */
export interface ContributionRecordV2Payload {
  v: number;           // schema version (2)
  r: string;           // Url
  i: string;           // TitleId Guid
  s: number;           // Status
  t: string | null;    // ThumbnailUrl
  m: string | null;    // Memo
  p: boolean;          // SeenInPopular
  e: boolean;          // SeenInLatest
  l: number | null;    // LastChapter (decimal)
  u: string | null;    // LastUpdateUTC
}

/** ContributionSourceEntity — canonical source definition. */
export interface ContributionSourceEntityPayload {
  i: string;           // SourceId Guid (MD5("package:sourceId"))
  p: string;           // Package
  pi?: string | null;  // PackageId Guid (MD5("package"))
  s: number;           // SourceId (long)
  n: string;           // SourceName
  l: string;           // SourceLanguage
  b: string | null;    // LastBatchExecutionUTC
  li?: string[] | null; // LatestIds (ordered series-entry ids of the last GetLatest run)
  v: number;           // 0 | -1
}

/** SourceSeriesEntity — many-to-many association (source ↔ series entry). */
export interface SourceSeriesEntityPayload {
  s: string;           // SourceId Guid (FK → sources.id)
  e: string;           // SeriesEntityId Guid (FK → series.id)
  v: number;           // 0 | -1
}

/** ContributionSeriesEntity — per-series source entry (identity = package:url). */
export interface ContributionSeriesEntityPayload {
  i: string;                       // SeriesId Guid (MD5("package:url"))
  m: string;                       // MappingId Guid
  s: string;                       // representative SourceId Guid (legacy; canonical set in source_series)
  pi?: string | null;              // PackageId Guid (MD5("package"))
  d: ContributionRecordV2Payload;  // embedded record
  z?: string | null;               // VerifiedDateUTC — null/absent = unverified
  q?: boolean;                     // Metadata — DERIVED: a live metadata row exists (true ≠ matched)
  v: number;                       // 0 | -1
}

/** ContributionMetadataEntity — metadata provider link. */
export interface ContributionMetadataEntityPayload {
  i: string;           // MetadataId Guid
  m: string;           // MappingId Guid
  p: number;           // ProviderId (ExternalSeriesProvider enum)
  k: string | null;    // ProviderKey
  s: number;           // MappingStatus (SeriesMappingStatus enum)
  u: string | null;    // LinkedDate
  v: number;           // 0 | -1
}

/**
 * Request body for POST /upload.
 * Field names are the one-letter JSON keys used by the backend.
 */
export interface UploadRequest {
  e?: number;   // SchemaVersion — must be 1
  u?: string;   // GeneratedUtc (ISO 8601)
  v?: number;   // collection Replication Version Number (client baseline)
  t?: TitleEntityPayload[];                     // titles
  m?: MappingTitleEntityPayload[];              // mapping_titles
  s?: ContributionSourceEntityPayload[];        // sources (canonical)
  x?: SourceSeriesEntityPayload[];              // source ↔ series associations
  i?: ContributionSeriesEntityPayload[];        // series (per-series entries)
  d?: ContributionMetadataEntityPayload[];      // metadata
}

/**
 * Request body for POST /admin/ban.
 */
export interface BanRequest {
  contributor_id: string;
  ban_reason: string;
}
