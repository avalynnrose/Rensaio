/**
 * Run the daily export job.
 *
 *  1. Scrub archived records older than the retention window (hard-delete).
 *  2. Archive orphaned titles (no active references in series/metadata/mapping).
 *  3. Bump the Replication Version Number.
 *  4. Stream all active entity rows into the streamable export engine
 *     (protobuf → tag+compress → AES-256 → base64) and push the single
 *     `metadata.bin` file to the configured GitHub repository, plus a
 *     `metadata.bin.sha256` sidecar (base64 SHA-256 of the decoded binary)
 *     so clients can skip the import when the file is unchanged.
 */
import type { Env } from '../types';
import { ARCHIVE_RETENTION_DAYS } from '../types';
import type {
  ContributionMetadataEntityPayload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  SourceSeriesEntityPayload,
  TitleEntityPayload,
} from '../models/requests';
import { buildMetadataBin, type ExportRowSets } from './export-engine';
import {
  bumpReplicationVersion,
  clearPendingChanges,
  getReplicationVersion,
  hasPendingChanges,
} from './replication-service';
import { resolveCompression } from '../utils/compression';

/**
 * Claim the export lock (single-slot) atomically. Returns true when THIS
 * invocation acquired it (INSERT succeeded). Concurrent invocations (cron +
 * manual /admin/export, or double-triggered crons) that would otherwise:
 *   - double-bump the Replication Version Number,
 *   - race the clearPendingChanges,
 *   - or both try to push to GitHub with a stale sha (409 conflicts)
 * are serialized: only one run proceeds, the others become cheap no-ops.
 */
async function acquireExportLock(db: D1Database): Promise<boolean> {
  const now = new Date().toISOString();
  const res = await db
    .prepare(
      `INSERT OR IGNORE INTO export_lock (id, acquired_utc) VALUES (1, ?)`
    )
    .bind(now)
    .run();
  // INSERT OR IGNORE reports changes = 1 when inserted, 0 when the slot already exists.
  return res.meta.changes === 1;
}

async function releaseExportLock(db: D1Database): Promise<void> {
  await db.prepare('DELETE FROM export_lock WHERE id = 1').run();
}

/**
 * Run the export.
 *
 * @param force when true, bypass the `hasPendingChanges` gate so a version bump
 *   + `metadata.bin` push ALWAYS happens. Intended for admin recovery (e.g. the
 *   exporter code changed and the already-published file must be regenerated
 *   even though no data changed). The daily cron and the default admin export
 *   pass `false`, preserving the "skip when nothing is pending" behaviour.
 */
export async function runDailyExport(env: Env, force = false): Promise<{
  scrubbed: { sources: number; metadata: number; titles: number };
  orphanTitlesArchived: number;
  files: string[];
  version: number;
  compression: number;
  sha256: string;
  exported: boolean;
}> {
  // Serialize exports: if another run (cron/manual overlap) holds the lock,
  // this invocation returns immediately and does NOT touch the version/flag.
  if (!(await acquireExportLock(env.DB))) {
    return {
      scrubbed: { sources: 0, metadata: 0, titles: 0 },
      orphanTitlesArchived: 0,
      files: [],
      version: await getReplicationVersion(env.DB),
      compression: -1,
      sha256: '',
      exported: false,
    };
  }

  try {
    return await runExportLocked(env, force);
  } finally {
    await releaseExportLock(env.DB);
  }
}

async function runExportLocked(env: Env, force = false): Promise<{
  scrubbed: { sources: number; metadata: number; titles: number };
  orphanTitlesArchived: number;
  files: string[];
  version: number;
  compression: number;
  sha256: string;
  exported: boolean;
}> {
  const scrubbed = await scrubArchived(env.DB);

  // Titles are never archived by removes/scrubs; archive the ones with no
  // active references so the export stays bounded.
  const orphanTitlesArchived = await archiveOrphanTitles(env.DB);

  // 3. Skip the export entirely when no contributor changes are pending.
  //    The version is only bumped (and metadata.bin only pushed) when at least
  //    one upload / admin mutation applied rows since the last export.
  //    `force` (admin recovery, e.g. after an exporter code fix) bypasses this
  //    gate so a fresh metadata.bin is always published.
  if (!force && !(await hasPendingChanges(env.DB))) {
    return {
      scrubbed,
      orphanTitlesArchived,
      files: [],
      version: await getReplicationVersion(env.DB),
      compression: -1, // nothing exported
      sha256: '',
      exported: false,
    };
  }

  // 3.5 Bump the Replication Version Number only when exporting changes.
  const version = await bumpReplicationVersion(env.DB);

  // 4. Load all active rows.
  const rows = await loadAllRows(env.DB);

  const generatedUtc = new Date().toISOString();

  // Build the encrypted metadata.bin payload (protobuf → tag+compress → AES).
  const payload = await buildMetadataBin(
    rows,
    version,
    generatedUtc,
    env.AESKEY256IV,
    env.EXPORT_COMPRESSION
  );

  // Push the file + its SHA-256 sidecar. The sidecar holds the base64-encoded SHA-256 of the
  // DECODED BINARY bytes (exactly what GitHub stores/serves as metadata.bin), so clients can
  // decode the raw they pull, hash it, and skip the import when the file is unchanged.
  await pushFileToGitHub(env, 'metadata.bin', payload.base64);
  await pushFileToGitHub(env, 'metadata.bin.sha256', payload.sha256);

  // Stamp every active row with the version we JUST exported. Without this, a
  // generation that was stamped with an older version (e.g. the big upload that
  // landed at v6 but whose v6 export never pushed) would remain
  // `replication_version < current` forever, so hasPendingChanges() would keep
  // returning true and re-export the same dataset every day even after it is
  // published. Stamping makes the data-derived check converge: after this run,
  // active rows carry the current version and nothing is pending until the next
  // upload bumps a newer version in.
  await stampExportedRows(env.DB, version);

  // Clear the pending flag only AFTER the files were published successfully,
  // so a failed push keeps pending_changes = 1 and the next run retries.
  await clearPendingChanges(env.DB);

  const compression = resolveCompression(env.EXPORT_COMPRESSION);

  return {
    scrubbed,
    orphanTitlesArchived,
    files: ['metadata.bin', 'metadata.bin.sha256'],
    version,
    compression,
    sha256: payload.sha256,
    exported: true,
  };
}

/**
 * Load every active row (archived_at IS NULL) as export payloads.
 * Exported so the verified-contributor snapshot route can reuse the same
 * canonical row → payload mapping as the daily export.
 */
export async function loadAllRows(db: D1Database): Promise<ExportRowSets> {
  const titles = await db
    .prepare('SELECT id, title, replication_version FROM titles WHERE archived_at IS NULL')
    .all<{ id: string; title: string; replication_version: number | null }>();
  const mappingTitles = await db
    .prepare('SELECT mapping_id, title_id FROM mapping_titles WHERE archived_at IS NULL')
    .all<{ mapping_id: string; title_id: string }>();
  const sources = await db
    .prepare(
      `SELECT id, package, package_id, source_id, source_name, source_language, last_batch_utc, latest_ids, replication_version
       FROM sources WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      package: string;
      package_id: string | null;
      source_id: number;
      source_name: string;
      source_language: string;
      last_batch_utc: string | null; latest_ids: string | null;
      replication_version: number | null;
    }>();
  const sourceSeries = await db
    .prepare(
      `SELECT source_id, series_entity_id, replication_version
       FROM source_series WHERE archived_at IS NULL`
    )
    .all<{
      source_id: string;
      series_entity_id: string;
      replication_version: number | null;
    }>();
  const series = await db
    .prepare(
      `SELECT id, mapping_id, source_id, package_id, title_id, url, thumbnail_url, status, memo,
              seen_in_popular, seen_in_latest, last_chapter, last_update_utc, verified_date_utc,
              metadata_flag, replication_version
       FROM series WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      mapping_id: string;
      source_id: string;
      package_id: string | null;
      title_id: string;
      url: string;
      thumbnail_url: string | null;
      status: number;
      memo: string | null;
      seen_in_popular: number;
      seen_in_latest: number;
      last_chapter: number | null;
      last_update_utc: string | null;
      verified_date_utc: string | null;
      metadata_flag: number;
      replication_version: number | null;
    }>();
  const metadata = await db
    .prepare(
      `SELECT id, mapping_id, provider_id, provider_key, mapping_status, linked_date, replication_version
       FROM metadata WHERE archived_at IS NULL`
    )
    .all<{
      id: string;
      mapping_id: string;
      provider_id: number;
      provider_key: string | null;
      mapping_status: number;
      linked_date: string | null;
      replication_version: number | null;
    }>();

  return {
    titles: (titles.results ?? []).map<TitleEntityPayload>((r) => ({
      i: r.id,
      t: r.title,
      v: r.replication_version ?? 0,
    })),
    mappingTitles: (mappingTitles.results ?? []).map<MappingTitleEntityPayload>((r) => ({
      m: r.mapping_id,
      t: r.title_id,
      v: 0,
    })),
    sources: (sources.results ?? []).map<ContributionSourceEntityPayload>((r) => ({
      i: r.id,
      p: r.package,
      pi: r.package_id,
      s: r.source_id,
      n: r.source_name,
      l: r.source_language,
      b: r.last_batch_utc,
      li: r.latest_ids ? JSON.parse(r.latest_ids) : null,
      v: r.replication_version ?? 0,
    })),
    sourceSeries: (sourceSeries.results ?? []).map<SourceSeriesEntityPayload>((r) => ({
      s: r.source_id,
      e: r.series_entity_id,
      v: r.replication_version ?? 0,
    })),
    series: (series.results ?? []).map<ContributionSeriesEntityPayload>((r) => ({
      i: r.id,
      m: r.mapping_id,
      s: r.source_id,
      pi: r.package_id,
      d: {
        v: 2, // ContributionRecordV2 schema version
        r: r.url,
        i: r.title_id,
        s: r.status,
        t: r.thumbnail_url,
        m: r.memo,
        p: r.seen_in_popular === 1,
        e: r.seen_in_latest === 1,
        l: r.last_chapter,
        u: r.last_update_utc,
      },
      z: r.verified_date_utc,
      q: r.metadata_flag === 1,
      v: r.replication_version ?? 0,
    })),
    metadata: (metadata.results ?? []).map<ContributionMetadataEntityPayload>((r) => ({
      i: r.id,
      m: r.mapping_id,
      p: r.provider_id,
      k: r.provider_key,
      s: r.mapping_status,
      u: r.linked_date,
      v: r.replication_version ?? 0,
    })),
  };
}

/**
 * Stamp every ACTIVE row with the replication version that was just exported.
 *
 * This makes the data-derived pending check (hasPendingChanges) converge:
 * rows from a generation that was never published (e.g. uploaded at v6 but the
 * v6 export failed to push) keep `replication_version < current` until a push
 * actually succeeds — then they're re-stamped to `current`, so the next export
 * is a no-op until new rows arrive. Runs in one D1 batch.
 */
export async function stampExportedRows(db: D1Database, exportedVersion: number): Promise<void> {
  const now = new Date().toISOString();

  // Only re-stamp rows still carrying an OLDER generation (they were just
  // included in this export). Rows already at `exportedVersion` (freshly
  // uploaded after the bump) are left alone — they're pending for the NEXT
  // export, not this one.
  await db.batch([
    db
      .prepare(
        `UPDATE titles SET replication_version = ? WHERE archived_at IS NULL
           AND (replication_version IS NULL OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(exportedVersion, exportedVersion),
    db
      .prepare(
        `UPDATE sources SET replication_version = ? WHERE archived_at IS NULL
           AND (replication_version IS NULL OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(exportedVersion, exportedVersion),
    db
      .prepare(
        `UPDATE series SET replication_version = ? WHERE archived_at IS NULL
           AND (replication_version IS NULL OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(exportedVersion, exportedVersion),
    db
      .prepare(
        `UPDATE source_series SET replication_version = ? WHERE archived_at IS NULL
           AND (replication_version IS NULL OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(exportedVersion, exportedVersion),
    db
      .prepare(
        `UPDATE metadata SET replication_version = ? WHERE archived_at IS NULL
           AND (replication_version IS NULL OR (replication_version >= 0 AND replication_version < ?))`
      )
      .bind(exportedVersion, exportedVersion),
  ]);

  // update last_change so consumers/dashboards see the re-stamp timestamp.
  await db
    .prepare('UPDATE replication SET bumped_at = ? WHERE id = ?')
    .bind(now, 1)
    .run();
}

/**
 * Archive titles that no longer have any active references.
 */
export async function archiveOrphanTitles(db: D1Database): Promise<number> {
  const now = new Date().toISOString();
  const result = await db
    .prepare(
      `UPDATE titles
       SET archived_at = ?
       WHERE archived_at IS NULL
         AND NOT EXISTS (
           SELECT 1 FROM series WHERE series.title_id = titles.id AND series.archived_at IS NULL
         )
         AND NOT EXISTS (
           SELECT 1 FROM mapping_titles WHERE mapping_titles.title_id = titles.id AND mapping_titles.archived_at IS NULL
         )`
    )
    .bind(now)
    .run();

  return result.meta.changes;
}

/**
 * Permanently delete records soft-deleted more than ARCHIVE_RETENTION_DAYS ago.
 * Ordering matters: children (series, mapping_titles, metadata) first, then
 * parents (sources, titles, mappings).
 */
export async function scrubArchived(db: D1Database): Promise<{
  sources: number;
  metadata: number;
  titles: number;
}> {
  // Associations first (child of both sources and series).
  await db
    .prepare(
      `DELETE FROM source_series
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  await db
    .prepare(
      `DELETE FROM series
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  await db
    .prepare(
      `DELETE FROM mapping_titles
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const metadata = await db
    .prepare(
      `DELETE FROM metadata
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const sources = await db
    .prepare(
      `DELETE FROM sources
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const titles = await db
    .prepare(
      `DELETE FROM titles
       WHERE archived_at IS NOT NULL
         AND archived_at < datetime('now', ?)`
    )
    .bind(`-${ARCHIVE_RETENTION_DAYS} days`)
    .run();

  const orphans = await db
    .prepare(
      `DELETE FROM titles
       WHERE archived_at IS NULL
         AND NOT EXISTS (SELECT 1 FROM series WHERE series.title_id = titles.id)
         AND NOT EXISTS (SELECT 1 FROM mapping_titles WHERE mapping_titles.title_id = titles.id)`
    )
    .run();

  return {
    sources: sources.meta.changes,
    metadata: metadata.meta.changes,
    titles: titles.meta.changes + orphans.meta.changes,
  };
}

/**
 * Push a file to the target GitHub repository via the Contents API.
 * `content` is already base64 (the encrypted metadata.bin stream).
 */
async function pushFileToGitHub(env: Env, fileName: string, base64Content: string): Promise<void> {
  const repo = env.EXPORT_GITHUB_REPO;
  const path = buildExportPath(env.EXPORT_PATH, fileName);
  const api = `https://api.github.com/repos/${repo}/contents/${path}`;
  const headers: Record<string, string> = {
    Authorization: `Bearer ${env.GITHUB_TOKEN}`,
    Accept: 'application/vnd.github+json',
    'User-Agent': 'rensaio-contribution-db',
  };

  // Get the current sha if the file exists (handles >1MB via git trees API).
  const sha = await getFileSha(env, path, headers);

  const body = {
    message: `Daily contribution export: ${fileName}`,
    content: base64Content,
    ...(sha ? { sha } : {}),
  };

  const response = await fetch(api, {
    method: 'PUT',
    headers: { ...headers, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    const detail = await response.text();
    throw new Error(`GitHub push failed for ${fileName}: ${response.status} ${detail}`);
  }
}

/**
 * Resolve the current blob sha of a file in the export repository.
 * (Unchanged from the original — robust to >1MB blobs via the git trees API.)
 */
async function getFileSha(
  env: Env,
  path: string,
  headers: Record<string, string>
): Promise<string | undefined> {
  const repo = env.EXPORT_GITHUB_REPO;
  const api = `https://api.github.com/repos/${repo}/contents/${path}`;

  const existing = await fetch(api, { headers });
  if (existing.ok) {
    const body = (await existing.json()) as { sha?: string };
    return body.sha;
  }

  if (existing.status !== 404) {
    return undefined;
  }

  // The file may be >1MB (or genuinely absent). Resolve its sha via the git
  // trees API. Absent files simply yield no entry.
  const commit = await fetch(`https://api.github.com/repos/${repo}/commits/HEAD`, { headers });
  if (!commit.ok) {
    return undefined;
  }
  const commitBody = (await commit.json()) as { commit?: { tree?: { sha?: string } } };
  const treeSha = commitBody.commit?.tree?.sha;
  if (!treeSha) {
    return undefined;
  }

  const tree = await fetch(
    `https://api.github.com/repos/${repo}/git/trees/${treeSha}?recursive=1`,
    { headers }
  );
  if (!tree.ok) {
    return undefined;
  }
  const treeBody = (await tree.json()) as { tree?: Array<{ path?: string; sha?: string }> };
  return treeBody.tree?.find((entry) => entry.path === path)?.sha;
}

/**
 * Join the export root and a file name into a repo-relative path.
 */
function buildExportPath(exportPath: string, fileName: string): string {
  const normalized = exportPath.replace(/^\/+|\/+$/g, '');
  return normalized ? `${normalized}/${fileName}` : fileName;
}
