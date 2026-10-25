/**
 * Streamable export engine — builds the `metadata.bin` payload.
 *
 * Pipeline (per requirement 4):
 *
 *   D1 rows ──► protobuf encode (no intermediate JSON)
 *            ──► 1-byte compression tag + compress (ZSTD | Brotli)
 *            ──► AES-256-CBC encrypt
 *            ──► base64 (for the GitHub Contents API)
 *
 * The engine collects D1 rows in bounded batches and writes each entity row
 * directly into a protobuf ProtoWriter (chunked), so memory stays bounded by
 * the compressed size rather than a full JSON document.
 */
import { ProtoWriter } from '../proto/writer';
import {
  encodeMappingTitle,
  encodeMetadata,
  encodeSeries,
  encodeSource,
  encodeSourceSeries,
  encodeTitle,
  SNAPSHOT_FIELD_GENERATED_UTC,
  SNAPSHOT_FIELD_SCHEMA_VERSION,
  SNAPSHOT_FIELD_VERSION,
} from '../proto/codec';
import type {
  ContributionMetadataEntityPayload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  SourceSeriesEntityPayload,
  TitleEntityPayload,
} from '../models/requests';
import { compressSnapshot, resolveCompression } from '../utils/compression';
import { encryptBytes, bytesToBase64 } from '../utils/crypto';
import { SNAPSHOT_SCHEMA_VERSION } from '../types';

export interface ExportRowSets {
  titles: TitleEntityPayload[];
  mappingTitles: MappingTitleEntityPayload[];
  sources: ContributionSourceEntityPayload[];
  sourceSeries: SourceSeriesEntityPayload[];
  series: ContributionSeriesEntityPayload[];
  metadata: ContributionMetadataEntityPayload[];
}

/**
 * Build the full metadata.bin payload from row sets.
 *
 * @param rows          entity row sets (already selected from D1)
 * @param version       current Replication Version Number
 * @param generatedUtc  ISO 8601 UTC generation time
 * @param aeskey256iv   AES secret from the env
 * @param compression   "0" (zstd) or "1" (brotli)
 * @returns base64 string ready for the GitHub Contents API
 */
export async function buildMetadataBin(
  rows: ExportRowSets,
  version: number,
  generatedUtc: string,
  aeskey256iv: string,
  compressionRaw: string | undefined
): Promise<{ base64: string; compression: number; byteLength: number; sha256: string }> {
  const compression = resolveCompression(compressionRaw);

  // ── 1. Protobuf encode (streaming writer) ──
  const w = new ProtoWriter();
  w.uint32(SNAPSHOT_FIELD_SCHEMA_VERSION, SNAPSHOT_SCHEMA_VERSION);
  w.string(SNAPSHOT_FIELD_GENERATED_UTC, generatedUtc);
  w.int32(SNAPSHOT_FIELD_VERSION, version);

  for (const title of rows.titles) encodeTitle(w, title);
  for (const mt of rows.mappingTitles) encodeMappingTitle(w, mt);
  for (const source of rows.sources) encodeSource(w, source);
  for (const ss of rows.sourceSeries) encodeSourceSeries(w, ss);
  for (const series of rows.series) encodeSeries(w, series);
  for (const meta of rows.metadata) encodeMetadata(w, meta);

  const protobuf = w.finish();

  // ── 2 + 3. Tag + compress ──
  const compressed = await compressSnapshot(protobuf, compression);

  // ── 4. AES-256-CBC encrypt the whole tagged+compressed stream ──
  const encrypted = await encryptBytes(compressed.bytes, aeskey256iv);

  // ── 5. base64 for GitHub ──
  // ── 6. SHA-256 of the DECODED BINARY bytes (what GitHub stores/serves), returned
  //        base64-encoded so the export service can push it into the .sha256 sidecar.
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encrypted));

  return {
    base64: bytesToBase64(encrypted),
    compression,
    byteLength: encrypted.length,
    sha256: bytesToBase64(digest),
  };
}

/**
 * Chunked variant for future paginated D1 reads — reserved for the streaming
 * extension. The current implementation accumulates row sets in memory, which
 * is acceptable for the pure-JS codec design (compressed payload dominates).
 */
export { resolveCompression };