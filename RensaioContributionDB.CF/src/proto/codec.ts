/**
 * Typed encoder for the ContributionSnapshotV1 protobuf wire format.
 *
 * Maps the JSON payload types from `src/models/requests.ts` onto the .proto
 * field numbers in `proto/contribution_snapshot_v1.proto`. Used by the export
 * engine to stream entity rows directly into protobuf (no intermediate JSON).
 */
import { ProtoWriter } from './writer';
import type {
  ContributionMetadataEntityPayload,
  ContributionRecordV2Payload,
  ContributionSeriesEntityPayload,
  ContributionSourceEntityPayload,
  MappingTitleEntityPayload,
  SourceSeriesEntityPayload,
  TitleEntityPayload,
} from '../models/requests';

// Field numbers in ContributionSnapshotV1 (see proto file).
export const SNAPSHOT_FIELD_SCHEMA_VERSION = 1;
export const SNAPSHOT_FIELD_GENERATED_UTC = 2;
export const SNAPSHOT_FIELD_VERSION = 3;
export const SNAPSHOT_FIELD_TITLES = 4;
export const SNAPSHOT_FIELD_MAPPING_TITLES = 5;
export const SNAPSHOT_FIELD_SOURCES = 6;
export const SNAPSHOT_FIELD_SERIES = 7;
export const SNAPSHOT_FIELD_METADATA = 8;
export const SNAPSHOT_FIELD_SOURCE_SERIES = 9;

// Shared scratch writer for nested per-row messages. Each encodeX is fully
// synchronous and consumes the scratch via finish() into the parent BEFORE the
// next row, so reusing one instance is safe and avoids allocating a fresh
// ProtoWriter (with its own growth buffer) per row — which is the difference
// between O(rows × chunk) and O(payload) memory/CPU.
const scratch = new ProtoWriter();

/** Reset the shared scratch (cheap: just resets offset; buffer reused). */
function scrub(): void {
  scratch.reset();
}

/** Encode a TitleEntity row. */
export function encodeTitle(w: ProtoWriter, row: TitleEntityPayload): void {
  scrub();
  scratch.string(1, row.i);
  scratch.string(2, row.t);
  scratch.int32(3, row.v);
  w.message(SNAPSHOT_FIELD_TITLES, scratch.finish());
}

/** Encode a MappingTitleEntity row. */
export function encodeMappingTitle(w: ProtoWriter, row: MappingTitleEntityPayload): void {
  scrub();
  scratch.string(1, row.m);
  scratch.string(2, row.t);
  scratch.int32(3, row.v);
  w.message(SNAPSHOT_FIELD_MAPPING_TITLES, scratch.finish());
}

/** Encode a ContributionSourceEntity row. */
export function encodeSource(w: ProtoWriter, row: ContributionSourceEntityPayload): void {
  scrub();
  scratch.string(1, row.i);
  scratch.string(2, row.p);
  scratch.int64(3, row.s);
  scratch.string(4, row.n);
  scratch.string(5, row.l);
  scratch.string(6, row.b);
  scratch.repeatedString(8, row.li);
  scratch.int32(7, row.v);
  // field 9 — optional PackageId (omitted when absent so pre-normalization rows decode as null).
  scratch.string(9, row.pi);
  w.message(SNAPSHOT_FIELD_SOURCES, scratch.finish());
}

/** Encode a SourceSeriesEntity row (many-to-many source ↔ series association). */
export function encodeSourceSeries(w: ProtoWriter, row: SourceSeriesEntityPayload): void {
  scrub();
  scratch.string(1, row.s);
  scratch.string(2, row.e);
  scratch.int32(3, row.v);
  w.message(SNAPSHOT_FIELD_SOURCE_SERIES, scratch.finish());
}

/** Encode a ContributionRecordV2 (field numbers per proto message). */
export function encodeContributionRecord(w: ProtoWriter, row: ContributionRecordV2Payload): void {
  w.uint32(1, row.v);
  w.string(2, row.i);
  w.string(3, row.t);
  w.int32(4, row.s);
  w.bool(5, row.p);
  w.bool(6, row.e);
  w.double(7, row.l);
  w.string(8, row.u);
  w.string(9, row.r);
  w.string(10, row.m);
}

/** Encode a ContributionSeriesEntity row (embeds the record). */
export function encodeSeries(w: ProtoWriter, row: ContributionSeriesEntityPayload): void {
  scrub();
  scratch.string(1, row.i);
  scratch.string(2, row.m);
  scratch.string(3, row.s);
  const rec = new ProtoWriter(); // record writer — small fields, buffer reuses scratch-sizes
  encodeContributionRecord(rec, row.d);
  scratch.message(4, rec.finish());
  scratch.int32(5, row.v);
  // field 6 — optional VerifiedDateUTC (omitted when null, so the cloud keeps its stored value).
  scratch.string(6, row.z);
  // field 7 — derived Metadata flag (true = a live metadata row exists). Always emitted so the
  // decoder sees an explicit false rather than an absent field.
  scratch.bool(7, row.q === true);
  // field 8 — optional PackageId (omitted when absent so pre-normalization rows decode as null).
  scratch.string(8, row.pi);
  w.message(SNAPSHOT_FIELD_SERIES, scratch.finish());
}

/** Encode a ContributionMetadataEntity row. */
export function encodeMetadata(w: ProtoWriter, row: ContributionMetadataEntityPayload): void {
  scrub();
  scratch.string(1, row.i);
  scratch.string(2, row.m);
  scratch.int32(3, row.p);
  scratch.string(4, row.k);
  scratch.int32(5, row.s);
  scratch.string(6, row.u);
  scratch.int32(7, row.v);
  w.message(SNAPSHOT_FIELD_METADATA, scratch.finish());
}
