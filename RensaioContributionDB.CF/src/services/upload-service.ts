/**
 * Process a ContributionSnapshotV1 upload batch for a single contributor.
 *
 * Each entity row carries a `v` (replication version) field:
 *   - 0  → add/update (UPSERT by identity key)
 *   - -1 → logical delete (tombstone → set archived_at)
 *
 * The five entity lists map to D1 tables:
 *   t → titles            (TitleEntity; id = MD5(normalized title))
 *   m → mapping_titles    (MappingTitleEntity)
 *   s → sources           (ContributionSourceEntity)
 *   i → series            (ContributionSeriesEntity — record flattened)
 *   d → metadata          (ContributionMetadataEntity)
 *
 * Mapping identity resolution (semantic, idempotent):
 *   client mapping uuids are TRANSIENT intra-batch references. Before writing any
 *   row we resolve each client mapping id to a CLOUD mapping id by looking for an
 *   existing cloud mapping whose active mapping_titles overlap the batch's title
 *   set for that client mapping (the same fuzzy rule the backend's
 *   ResolveMappingAsync uses). If an overlap is found the client rows are folded
 *   into the existing cloud mapping — two contributors tracking the same series
 *   converge on ONE mappings row. Otherwise the client uuid is adopted as the
 *   cloud mapping id.
 *
 * Metadata identity (semantic dedup):
 *   the unique key is the triple (mapping_id, provider_id, provider_key)
 *   (migration 0005). `id` is a cloud-internal identifier: on conflict it is
 *   KEPT (not in the SET clause) and the row is updated in place. mapping_status
 *   merges by priority — UserConfirmed > Blocked > AutoMatched > ForeverIgnored >
 *   ManualIgnore > Unmatched — so a manual decision can never be clobbered by
 *   a weaker automated one; linked_date / contributor_id are last-writer-wins.
 *
 * Mappings referenced by `m`/`i`/`d` rows are created on demand from the
 * resolved mapping ids.
 *
 * All valid statements run in a single D1 batch (atomic). Entity-level
 * validation errors are collected and returned.
 */
import type {
  ContributionMetadataEntityPayload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  SourceSeriesEntityPayload,
  TitleEntityPayload,
} from '../models/requests';
import type { UploadError, UploadResponse } from '../models/responses';
import {
  REPLICATION_ADD_OR_UPDATE,
  REPLICATION_DELETE,
  SNAPSHOT_SCHEMA_VERSION,
  SUPPORTED_ENTITY_TYPES,
} from '../types';
import { markPendingChanges } from './replication-service';

const TITLES = 't';
const MAPPING_TITLES = 'm';
const SOURCES = 's';
const SOURCE_SERIES = 'x';
const SERIES = 'i';
const METADATA = 'd';

/** Maximum statements per D1 batch (Cloudflare limit is 100). */
const BATCH_CHUNK_SIZE = 95;

/**
 * Track provenance of every statement so a failed execution can be reported
 * with the exact entity list + row index it belongs to.
 */
interface StmtRef {
  list: string;
  index: number;
  stmt: D1PreparedStatement;
}

/**
 * A single D1 batch result row. D1 emits one entry per statement; a statement
 * failure is reported per-entry (success:false + error) and does NOT throw.
 */
interface BatchResult {
  success?: boolean;
  error?: string;
  results?: unknown[];
  meta?: { changes?: number };
}

export async function processUpload(
  db: D1Database,
  contributorId: string,
  body: {
    e?: number;
    u?: string;
    v?: number;
    t?: TitleEntityPayload[];
    m?: MappingTitleEntityPayload[];
    s?: ContributionSourceEntityPayload[];
    x?: SourceSeriesEntityPayload[];
    i?: ContributionSeriesEntityPayload[];
    d?: ContributionMetadataEntityPayload[];
  }
): Promise<UploadResponse> {
  const now = new Date().toISOString();
  const errors: UploadError[] = [];
  const refs: StmtRef[] = [];

  // ── 0. Schema version gate ──
  if (body.e !== undefined && body.e !== SNAPSHOT_SCHEMA_VERSION) {
    return {
      processed: 0,
      errors: [
        { list: 'e', index: 0, message: `Unsupported schema version: ${body.e}` },
      ],
    };
  }

  // ─ 0.25 Fetch the current replication version from the singleton. Every row
  // written by this upload is stamped with it, so the snapshot's per-row `v`
  // reflects "which replication generation produced this row".
  let replicationVersion: number;
  try {
    const row = await db
      .prepare('SELECT version FROM replication WHERE id = 1')
      .first<{ version: number }>();
    replicationVersion = row?.version ?? 0;
  } catch (err) {
    console.error('Failed to read replication version, defaulting to 0:', err);
    replicationVersion = 0;
  }

  // ─ 0.5 Resolve client mapping ids → cloud mapping ids ──
  // Must happen before any statement is built so series/metadata/mapping_titles
  // all reference the same resolved id for a given client mapping.
  let mappingIdMap: Map<string, string> = new Map();
  try {
    mappingIdMap = await resolveMappingIds(db, body);
  } catch (err) {
    // Resolution is best-effort: on failure fall back to client uuids (worst
    // case a duplicate mapping row, no data loss). Surface as a warning.
    console.error('Mapping resolution failed, falling back to client ids:', err);
  }

  // ─ 1. Titles ──
  if (body.t) {
    for (let i = 0; i < body.t.length; i += 1) {
      const row = body.t[i];
      try {
        for (const stmt of buildTitleStatements(db, row, contributorId, replicationVersion, now)) {
          refs.push({ list: TITLES, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: TITLES, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 2. Mapping titles (implicitly create mappings) ──
  if (body.m) {
    for (let i = 0; i < body.m.length; i += 1) {
      const row = body.m[i];
      try {
        for (const stmt of buildMappingTitleStatements(
          db, row, mappingIdMap, contributorId, replicationVersion, now
        )) {
          refs.push({ list: MAPPING_TITLES, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: MAPPING_TITLES, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 3. Sources (canonical) ──
  if (body.s) {
    for (let i = 0; i < body.s.length; i += 1) {
      const row = body.s[i];
      try {
        for (const stmt of buildSourceStatements(db, row, contributorId, replicationVersion, now)) {
          refs.push({ list: SOURCES, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: SOURCES, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 3b. Source ↔ series associations ──
  if (body.x) {
    for (let i = 0; i < body.x.length; i += 1) {
      const row = body.x[i];
      try {
        for (const stmt of buildSourceSeriesStatements(db, row, contributorId, replicationVersion, now)) {
          refs.push({ list: SOURCE_SERIES, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: SOURCE_SERIES, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 4. Series ──
  if (body.i) {
    for (let i = 0; i < body.i.length; i += 1) {
      const row = body.i[i];
      try {
        for (const stmt of buildSeriesStatements(
          db, row, mappingIdMap, contributorId, replicationVersion, now
        )) {
          refs.push({ list: SERIES, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: SERIES, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 5. Metadata ──
  if (body.d) {
    for (let i = 0; i < body.d.length; i += 1) {
      const row = body.d[i];
      try {
        for (const stmt of buildMetadataStatements(
          db, row, mappingIdMap, contributorId, replicationVersion, now
        )) {
          refs.push({ list: METADATA, index: i, stmt });
        }
      } catch (err) {
        errors.push({ list: METADATA, index: i, message: errMessage(err) });
      }
    }
  }

  // ── 6. Execute in chunks, reconciling EVERY statement's actual result. ──
  // D1's batch() reports per-statement failures in the result array without
  // throwing — the old code ignored those and reported success for all. Now we
  // surface each failure with its entity list + row index so the client can
  // retry precisely the rows that failed.
  let processed = 0;
  for (let offset = 0; offset < refs.length; offset += BATCH_CHUNK_SIZE) {
    const chunk = refs.slice(offset, offset + BATCH_CHUNK_SIZE);
    const results = await db.batch(chunk.map((r) => r.stmt));
    for (let k = 0; k < chunk.length; k += 1) {
      const ref = chunk[k];
      const res = results[k] as BatchResult | undefined;

      let ok: boolean;
      let errorMsg: string | undefined;
      if (!res) {
        ok = false;
        errorMsg = 'No batch result for statement';
      } else if (res.success === false || res.error) {
        ok = false;
        errorMsg = res.error ?? `Statement failed (success=${res.success})`;
      } else {
        // success === true (or undefined) with no error → applied.
        ok = true;
      }

      if (ok) {
        processed += 1;
      } else {
        errors.push({ list: ref.list, index: ref.index, message: errorMsg ?? 'Unknown batch error' });
      }
    }
  }

  // 7. If at least one row was applied, flag pending changes so the next export
  //    (cron or manual) publishes a new version + metadata.bin. Best-effort:
  //    a failure to mark the flag must never fail the upload itself.
  if (processed > 0) {
    try {
      await markPendingChanges(db);
    } catch (err) {
      console.error('Failed to mark pending changes after upload:', err);
    }
  }

  return { processed, errors };
}

// ── Mapping identity resolution ──────────────────────────────────────────────

/**
 * Resolves each client mapping id referenced by the batch to a CLOUD mapping id.
 *
 * For a client mapping with title set T we look for an existing ACTIVE cloud
 * mapping whose `mapping_titles` overlap T (most-overlapping wins). Found →
 * reuse the cloud mapping id (dedup across contributors). Not found → the
 * client uuid is adopted as the cloud mapping id.
 */
async function resolveMappingIds(
  db: D1Database,
  body: {
    m?: MappingTitleEntityPayload[];
    i?: ContributionSeriesEntityPayload[];
    d?: ContributionMetadataEntityPayload[];
  }
): Promise<Map<string, string>> {
  const resolved = new Map<string, string>();

  // 1. Collect the client mapping ids and their title sets.
  const titlesByClientMapping = new Map<string, Set<string>>();
  for (const row of body.m ?? []) {
    let set = titlesByClientMapping.get(row.m);
    if (!set) {
      set = new Set();
      titlesByClientMapping.set(row.m, set);
    }
    set.add(row.t);
  }
  // Series / metadata reference mappings without defining their titles —
  // include them so they resolve too (empty set → adopt client uuid).
  const referenced = new Set<string>(titlesByClientMapping.keys());
  for (const row of body.i ?? []) referenced.add(row.m);
  for (const row of body.d ?? []) referenced.add(row.m);

  if (referenced.size === 0) return resolved;

  // 2. Collect all distinct title ids so we can load the relevant mapping_titles
  //    rows in a bounded number of queries (chunked IN lists).
  const allTitleIds = new Set<string>();
  for (const set of titlesByClientMapping.values()) {
    for (const t of set) allTitleIds.add(t);
  }

  const titleChunks = chunk([...allTitleIds], 40);
  const existingByTitle = new Map<string, string[]>();
  for (const chunk of titleChunks) {
    if (chunk.length === 0) continue;
    const placeholders = chunk.map(() => '?').join(',');
    const result = await db
      .prepare(
        `SELECT mapping_id, title_id
         FROM mapping_titles
         WHERE archived_at IS NULL AND title_id IN (${placeholders})`
      )
      .bind(...chunk)
      .all<{ mapping_id: string; title_id: string }>();
    for (const r of result.results ?? []) {
      let mappingIds = existingByTitle.get(r.title_id);
      if (!mappingIds) {
        mappingIds = [];
        existingByTitle.set(r.title_id, mappingIds);
      }
      mappingIds.push(r.mapping_id);
    }
  }

  // 3. For each client mapping, pick the cloud mapping with the most shared titles.
  for (const clientId of referenced) {
    const titleSet = titlesByClientMapping.get(clientId) ?? new Set<string>();
    if (titleSet.size === 0) {
      // No titles to match on — adopt the client uuid (ensureMapping creates it).
      resolved.set(clientId, clientId);
      continue;
    }

    const overlapCount = new Map<string, number>();
    for (const titleId of titleSet) {
      for (const cloudId of existingByTitle.get(titleId) ?? []) {
        overlapCount.set(cloudId, (overlapCount.get(cloudId) ?? 0) + 1);
      }
    }

    let bestCloud: string | undefined;
    let bestCount = 0;
    for (const [cloudId, count] of overlapCount) {
      if (count > bestCount) {
        bestCloud = cloudId;
        bestCount = count;
      }
    }

    resolved.set(clientId, bestCloud ?? clientId);
  }

  return resolved;
}

/** Resolve a client mapping id to the cloud mapping id (defaults to the client id). */
function resolveMapping(mappingIdMap: Map<string, string>, clientId: string): string {
  return mappingIdMap.get(clientId) ?? clientId;
}

// ── Titles ───────────────────────────────────────────────────────────────────

function buildTitleStatements(
  db: D1Database,
  row: TitleEntityPayload,
  contributorId: string,
  replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['i', 't', 'v']);
  if (row.v === REPLICATION_DELETE) {
    // Tombstone: keep replication_version = -1 (delete marker) so consumers can
    // distinguish a delete from an add at a given replication version.
    return [
      db
        .prepare(
          'UPDATE titles SET replication_version = ?, archived_at = ?, contributor_id = ? WHERE id = ?'
        )
        .bind(row.v, now, contributorId, row.i),
    ];
  }
  // Upsert by id (MD5 of normalized title). Stamp the current replication
  // version (not the client's 0) and the last contributor that touched it.
  return [
    db
      .prepare(
        `INSERT OR IGNORE INTO titles (id, title, replication_version, contributor_id, archived_at)
         VALUES (?, ?, ?, ?, NULL)
         ON CONFLICT(id) DO UPDATE SET
           title = excluded.title,
           replication_version = excluded.replication_version,
           contributor_id = excluded.contributor_id,
           archived_at = NULL`
      )
      .bind(row.i, row.t, replicationVersion, contributorId),
  ];
}

// ── Mapping titles ───────────────────────────────────────────────────────────

function buildMappingTitleStatements(
  db: D1Database,
  row: MappingTitleEntityPayload,
  mappingIdMap: Map<string, string>,
  _contributorId: string,
  _replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['m', 't', 'v']);
  const mappingId = resolveMapping(mappingIdMap, row.m);
  const ensureMapping = db
    .prepare('INSERT OR IGNORE INTO mappings (id) VALUES (?)')
    .bind(mappingId);
  if (row.v === REPLICATION_DELETE) {
    return [
      ensureMapping,
      db
        .prepare(
          'UPDATE mapping_titles SET last_change = ?, archived_at = ? WHERE mapping_id = ? AND title_id = ?'
        )
        .bind(now, now, mappingId, row.t),
    ];
  }
  // Single-ownership rule (mapping-conflict repair): a title can only belong to ONE mapping.
  // When a client uploads a title→mapping association for a title that was previously (wrongly)
  // merged under a DIFFERENT mapping, archive the stale association so the title stops leaking
  // across series. The upsert below then (re)establishes the association under the new mapping.
  // Crucially this makes resolveMappingIds (which only reads archived_at IS NULL rows) fold
  // future uploads of this title onto the SINGLE surviving mapping, ungluing merged titles.
  const releaseFromOtherMappings = db
    .prepare(
      `UPDATE mapping_titles SET archived_at = ?, last_change = ?
       WHERE title_id = ? AND mapping_id <> ? AND archived_at IS NULL`
    )
    .bind(now, now, row.t, mappingId);

  return [
    releaseFromOtherMappings,
    ensureMapping,
    db
      .prepare(
        `INSERT OR IGNORE INTO mapping_titles (mapping_id, title_id, last_change, archived_at)
         VALUES (?, ?, ?, NULL)
         ON CONFLICT(mapping_id, title_id) DO UPDATE SET last_change = excluded.last_change, archived_at = NULL`
      )
      .bind(mappingId, row.t, now),
  ];
}

// ── Sources (canonical) ──────────────────────────────────────────────────────

function buildSourceStatements(
  db: D1Database,
  row: ContributionSourceEntityPayload,
  contributorId: string,
  replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['i', 'p', 's', 'v']);
  if (row.v === REPLICATION_DELETE) {
    // Tombstone: architecture row; keep replication_version = -1.
    return [
      db
        .prepare(
          'UPDATE sources SET replication_version = ?, archived_at = ?, contributor_id = ? WHERE id = ?'
        )
        .bind(row.v, now, contributorId, row.i),
    ];
  }
  // Upsert by id; ON CONFLICT resurrects an archived row (archived_at = NULL).
  // Stamp contributor + replication version on every write.
  return [
    db
      .prepare(
        `INSERT INTO sources (
            id, package, package_id, source_id, source_name, source_language,
            last_batch_utc, latest_ids, contributor_id, replication_version, archived_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)
         ON CONFLICT(id) DO UPDATE SET
           package = excluded.package,
           package_id = COALESCE(excluded.package_id, package_id),
           source_id = excluded.source_id,
           source_name = excluded.source_name,
           source_language = excluded.source_language,
           last_batch_utc = excluded.last_batch_utc,
           latest_ids = excluded.latest_ids,
           contributor_id = excluded.contributor_id,
           replication_version = excluded.replication_version,
           archived_at = NULL`
      )
      .bind(
        row.i,
        row.p,
        row.pi ?? null,
        row.s,
        row.n ?? '',
        row.l ?? '',
        row.b ?? null,
        row.li ? JSON.stringify(row.li) : null,
        contributorId,
        replicationVersion
      ),
  ];
}

// ── Source ↔ series associations (many-to-many) ──────────────────────────────

function buildSourceSeriesStatements(
  db: D1Database,
  row: SourceSeriesEntityPayload,
  _contributorId: string,
  replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['s', 'e', 'v']);
  if (row.v === REPLICATION_DELETE) {
    return [
      db
        .prepare(
          'UPDATE source_series SET replication_version = ?, archived_at = ? WHERE source_id = ? AND series_entity_id = ?'
        )
        .bind(row.v, now, row.s, row.e),
    ];
  }
  // Upsert by composite key; ON CONFLICT resurrects an archived row. `contributor_id` is not a
  // column on source_series (the association is derived from sources/series membership).
  return [
    db
      .prepare(
        `INSERT INTO source_series (source_id, series_entity_id, replication_version, archived_at)
         VALUES (?, ?, ?, NULL)
         ON CONFLICT(source_id, series_entity_id) DO UPDATE SET
           replication_version = excluded.replication_version,
           archived_at = NULL`
      )
      .bind(row.s, row.e, replicationVersion),
  ];
}

// ── Series ───────────────────────────────────────────────────────────────────

function buildSeriesStatements(
  db: D1Database,
  row: ContributionSeriesEntityPayload,
  mappingIdMap: Map<string, string>,
  contributorId: string,
  replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['i', 'm', 's', 'd', 'v']);
  const rec = row.d;
  const mappingId = resolveMapping(mappingIdMap, row.m);
  const ensureMapping = db
    .prepare('INSERT OR IGNORE INTO mappings (id) VALUES (?)')
    .bind(mappingId);
  if (row.v === REPLICATION_DELETE) {
    return [
      ensureMapping,
      db
        .prepare(
          'UPDATE series SET replication_version = ?, archived_at = ?, contributor_id = ? WHERE id = ?'
        )
        .bind(row.v, now, contributorId, row.i),
    ];
  }
  return [
    ensureMapping,
    db
      .prepare(
        `INSERT INTO series
           (id, mapping_id, source_id, package_id, title_id, url, thumbnail_url, status, memo,
            seen_in_popular, seen_in_latest, last_chapter, last_update_utc, verified_date_utc,
            metadata_flag, contributor_id, replication_version, archived_at)
         VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL)
         ON CONFLICT(id) DO UPDATE SET
           mapping_id = excluded.mapping_id,
           source_id = excluded.source_id,
           package_id = COALESCE(excluded.package_id, package_id),
           title_id = excluded.title_id,
           url = excluded.url,
           thumbnail_url = excluded.thumbnail_url,
           status = excluded.status,
           memo = excluded.memo,
           seen_in_popular = excluded.seen_in_popular,
           seen_in_latest = excluded.seen_in_latest,
           last_chapter = excluded.last_chapter,
           last_update_utc = excluded.last_update_utc,
           -- Sticky verified_date_utc: a non-null incoming value ALWAYS wins (Verify propagates).
           -- A null incoming value clears the stored date ONLY when the record data actually
           -- changed (a real update → the series is no longer the verified snapshot); an
           -- unchanged row keeps its stored date, so a plain sweep can never un-verify a
           -- verified series, and a LOCAL-only clear (Un-Verify / Join / Split) never reaches
           -- the worker at all.
           verified_date_utc = CASE
             WHEN excluded.verified_date_utc IS NOT NULL THEN excluded.verified_date_utc
             WHEN title_id IS NOT excluded.title_id
               OR url IS NOT excluded.url
               OR thumbnail_url IS NOT excluded.thumbnail_url
               OR status IS NOT excluded.status
               OR memo IS NOT excluded.memo
               OR seen_in_popular IS NOT excluded.seen_in_popular
               OR seen_in_latest IS NOT excluded.seen_in_latest
               OR last_chapter IS NOT excluded.last_chapter
               OR last_update_utc IS NOT excluded.last_update_utc
               THEN excluded.verified_date_utc
               ELSE verified_date_utc
             END,
             -- Derived "a live metadata row exists" flag; last-writer-wins (never hand-edited, so
             -- the uploader's value is the freshest derived state).
             metadata_flag = excluded.metadata_flag,
             contributor_id = excluded.contributor_id,
           replication_version = excluded.replication_version,
           archived_at = NULL`
      )
      .bind(
        row.i,
        mappingId,
        row.s,
        row.pi ?? null,
        rec.i,
        rec.r ?? '',
        rec.t ?? null,
        rec.s ?? 0,
        rec.m ?? null,
        rec.p ? 1 : 0,
        rec.e ? 1 : 0,
        rec.l ?? null,
        rec.u ?? null,
        row.z ?? null,
        row.q ? 1 : 0,
        contributorId,
        replicationVersion
      ),
  ];
}

// ── Metadata ─────────────────────────────────────────────────────────────────

// The conflict target must match the migration-0005 PARTIAL unique index
// (WHERE archived_at IS NULL) exactly, or SQLite rejects the upsert.
const METADATA_INSERT = `INSERT INTO metadata
   (id, mapping_id, provider_id, provider_key, mapping_status, linked_date,
    contributor_id, replication_version, archived_at)
 VALUES (?, ?, ?, ?, ?, ?, ?, ?, NULL)
 ON CONFLICT(mapping_id, provider_id, provider_key) WHERE archived_at IS NULL DO UPDATE SET
   mapping_status = CASE
     -- Strongest intent survives; weaker uploads cannot clobber manual decisions.
     -- In DO UPDATE expressions, existing-row columns are referenced unqualified.
     WHEN mapping_status = 2 OR excluded.mapping_status = 2 THEN 2  -- UserConfirmed
     WHEN mapping_status = 5 OR excluded.mapping_status = 5 THEN 5  -- Blocked
     WHEN mapping_status = 1 OR excluded.mapping_status = 1 THEN 1  -- AutoMatched
     WHEN mapping_status = 4 OR excluded.mapping_status = 4 THEN 4  -- ForeverIgnored
     WHEN mapping_status = 3 OR excluded.mapping_status = 3 THEN 3  -- ManualIgnore
     ELSE 0                                                         -- Unmatched
   END,
   linked_date = excluded.linked_date,
   contributor_id = excluded.contributor_id,
   replication_version = excluded.replication_version,
   archived_at = NULL`;

function buildMetadataStatements(
  db: D1Database,
  row: ContributionMetadataEntityPayload,
  mappingIdMap: Map<string, string>,
  contributorId: string,
  replicationVersion: number,
  now: string
): D1PreparedStatement[] {
  validateRow(row, ['i', 'm', 'p']);
  const mappingId = resolveMapping(mappingIdMap, row.m);
  const providerKey = row.k ?? '';
  const ensureMapping = db
    .prepare('INSERT OR IGNORE INTO mappings (id) VALUES (?)')
    .bind(mappingId);
  if (row.v === REPLICATION_DELETE) {
    return [
      ensureMapping,
      db
        .prepare(
          `UPDATE metadata SET replication_version = ?, archived_at = ?, contributor_id = ?
           WHERE mapping_id = ? AND provider_id = ? AND provider_key = ?
             AND archived_at IS NULL`
        )
        .bind(row.v, now, contributorId, mappingId, row.p, providerKey),
    ];
  }
  // Replace semantics (mapping-conflict repair): a client that re-keyed this provider on the
  // mapping (wrong auto-link → correct/blocked) uploads the NEW provider_key with v=0. The
  // unique triple no longer matches the OLD row, so without this step the old (wrong) row would
  // survive forever as a second active row. Archive the stale key first (same D1 batch), then
  // the upsert below inserts/updates the corrected row. UserConfirmed / Blocked rows are never
  // clobbered by a weaker re-key.
  const archiveOldKey = db
    .prepare(
      `UPDATE metadata SET archived_at = ?, replication_version = ?, contributor_id = ?
       WHERE mapping_id = ? AND provider_id = ? AND provider_key <> ?
         AND archived_at IS NULL
         AND mapping_status NOT IN (2, 5)`
    )
    .bind(now, replicationVersion, contributorId, mappingId, row.p, providerKey);

  // The incoming client `id` is used only when no semantic row exists yet — on
  // conflict it is KEPT (not in the SET clause). Dedup is by the triple.
  return [
    archiveOldKey,
    ensureMapping,
    db
      .prepare(METADATA_INSERT)
      .bind(
        row.i,
        mappingId,
        row.p,
        providerKey,
        row.s ?? 0,
        row.u ?? null,
        contributorId,
        replicationVersion
      ),
  ];
}

// ── Validation helpers ───────────────────────────────────────────────────────

function validateRow(row: unknown, required: string[]): void {
  if (!row || typeof row !== 'object' || Array.isArray(row)) {
    throw new Error('Entity row must be an object');
  }
  const obj = row as Record<string, unknown>;
  const v = obj.v;
  if (v !== REPLICATION_ADD_OR_UPDATE && v !== REPLICATION_DELETE) {
    throw new Error(`Invalid replication version (v): ${String(v)} — must be 0 or -1`);
  }
  for (const key of required) {
    const val = obj[key];
    if (val === undefined || val === null || val === '') {
      throw new Error(`Missing required field "${key}"`);
    }
  }
}

function chunk<T>(items: T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) {
    out.push(items.slice(i, i + size));
  }
  return out;
}

function errMessage(err: unknown): string {
  return err instanceof Error ? err.message : 'Unknown error';
}

export { SUPPORTED_ENTITY_TYPES };
