/**
 * Streaming Protocol Buffers encoder — dependency-free, Worker-compatible.
 *
 * Written against `proto/contribution_snapshot_v1.proto` (field numbers stable).
 * The writer accumulates output into bounded chunks so very large snapshots do
 * not materialize one giant byte array; `finish()` concatenates once.
 *
 * Wire type encoding (standard protobuf):
 *   varint  — uint32/uint64/int32/int64 (int32 negative → sign-extended 64-bit)
 *   length-delimited — string / bytes / nested message
 *   fixed64 — double
 */

const DEFAULT_CHUNK_SIZE = 1024 * 1024; // 1 MiB (only for arbitrarily large payloads)
// Start small and grow — a 1 MiB pre-allocation per nested writer (the codec
// creates one ProtoWriter PER ROW) caused ~8.6 GB of cumulative allocation +
// GC (which is CPU on Cloudflare) for the current dataset. With an incremental
// buffer, memory & CPU are O(payload), not O(rows × 1 MiB).
const INITIAL_CAPACITY = 256; // bytes

/**
 * 64-bit varint of an unsigned value, little-endian base-128.
 *
 * MUST use BigInt: JS's bitwise operators (`>>>`, `&`) coerce to 32-bit, so the
 * previous `v >>>= 7` implementation silently truncated every value >= 2^32.
 * That corrupted `int64 source_id` fields (real Mihon extension ids can exceed
 * 2^32), which is why the backend decoded an empty/0 source_id. The value is
 * coerced to its unsigned 64-bit two's-complement form so negative inputs (i.e.
 * protobuf int32/int64 = -1 delete markers) emit the canonical 10-byte varint.
 */
function encodeVarint(value: number): Uint8Array {
  let v = BigInt(Math.trunc(value)) & 0xffffffffffffffffn;
  const out: number[] = [];
  do {
    let byte = Number(v & 0x7fn);
    v >>= 7n;
    if (v !== 0n) byte |= 0x80;
    out.push(byte);
  } while (v !== 0n);
  return Uint8Array.from(out);
}

/**
 * Protobuf int32: negative values are sign-extended to 64 bits then varint
 * encoded (10-byte form), matching reference implementations.
 *
 * `encodeVarint` already emits the unsigned 64-bit two's-complement form, so a
 * negative int32 (e.g. -1) becomes the canonical sign-extended 10-byte varint.
 */
function encodeInt32(value: number): Uint8Array {
  return encodeVarint(value);
}

/**
 * Protobuf uint32.
 *
 * `>>> 0` normalizes to an unsigned 32-bit value; without it, values in
 * [2^31, 2^32) would be seen as negative and sign-extended to 64 bits.
 */
function encodeUint32(value: number): Uint8Array {
  return encodeVarint(value >>> 0);
}

/**
 * Streaming writer that appends protobuf fields into bounded chunks.
 *
 * Allocates INCREMENTALLY (starts at INITIAL_CAPACITY and grows geometrically)
 * instead of pre-allocating a full 1 MiB buffer per instance. The codec creates
 * one writer per entity row; keeping the per-row allocation O(row size) instead
 * of O(1 MiB) is what makes the exporter scale to 10–100× the current dataset
 * without GC-dominated CPU.
 */
export class ProtoWriter {
  private readonly chunks: Uint8Array[] = [];
  private current: Uint8Array = new Uint8Array(INITIAL_CAPACITY);
  private offset = 0;

  /** Append raw bytes (already-encoded varint/field), copying if it fits. */
  private append(bytes: Uint8Array): void {
    const needed = bytes.length;
    if (needed + this.offset > this.current.length) {
      // Grow geometrically (2×) to amortize allocation cost. Flush full chunks
      // to bound memory and avoid one giant array for huge payloads.
      if (needed + this.offset > DEFAULT_CHUNK_SIZE) {
        // Far bigger than a chunk: flush the partial buffer and push the large
        // piece directly.
        //
        // CRITICAL: push a COPY, never the caller's buffer by reference. The
        // codec passes `scratch.finish()` — a subarray VIEW into the shared
        // scratch writer — and then REUSES that scratch for the next row. If we
        // aliased the view, the next `scratch.reset()` + writes would overwrite
        // the bytes already pushed here, corrupting every row that straddles the
        // 1 MiB boundary (the backend then fails to decode: "Invalid string
        // length" in ReadString()).
        const left = this.current.subarray(0, this.offset);
        if (left.length > 0) this.chunks.push(left);
        this.chunks.push(bytes.slice());
        this.current = new Uint8Array(INITIAL_CAPACITY);
        this.offset = 0;
        return;
      }
      // Grow current buffer (never shrink; reset to INITIAL for small fresh slabs).
      let newCap = this.current.length;
      while (newCap < needed + this.offset) newCap <<= 1;
      const grown = new Uint8Array(Math.min(newCap, DEFAULT_CHUNK_SIZE));
      grown.set(this.current.subarray(0, this.offset), 0);
      this.current = grown;
    }
    this.current.set(bytes, this.offset);
    this.offset += bytes.length;
  }

  /** varint field. */
  varint(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value));
  }

  /** int32 (sign-extended) field. */
  int32(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeInt32(value));
  }

  /** uint32 field. */
  uint32(field: number, value: number): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeUint32(value));
  }

  /** int64 field (low 64 bits). */
  int64(field: number, value: number): void {
    // No mask: `value & 0xffffffff_ffffffff` is a TRAP — that literal exceeds
    // 2^53, so JS's ToInt32 turns it into 0 and the AND yields 0 for EVERY
    // value (source_id was always written as 0 → "empty source_id"). encodeVarint
    // already reduces to the unsigned 64-bit form.
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value));
  }

  /** bool field. */
  bool(field: number, value: boolean): void {
    this.append(encodeVarint((field << 3) | 0));
    this.append(encodeVarint(value ? 1 : 0));
  }

  /** string field (UTF-8, length-delimited). */
  string(field: number, value: string | null | undefined): void {
    if (value === null || value === undefined) return;
    const bytes = new TextEncoder().encode(value);
    this.append(encodeVarint((field << 3) | 2));
    this.append(encodeVarint(bytes.length));
    this.append(bytes);
  }

  /** repeated string field (one tag per element). */
  repeatedString(field: number, values: string[] | null | undefined): void {
    if (!values) return;
    for (const v of values) this.string(field, v);
  }

  /** Embedded message field (length-delimited). */
  message(field: number, messageBytes: Uint8Array): void {
    this.append(encodeVarint((field << 3) | 2));
    this.append(encodeVarint(messageBytes.length));
    this.append(messageBytes);
  }

  /** Fixed 8-byte double field (little-endian). */
  double(field: number, value: number | null | undefined): void {
    if (value === null || value === undefined) return;
    const buf = new ArrayBuffer(8);
    new DataView(buf).setFloat64(0, value, true);
    this.append(encodeVarint((field << 3) | 1));
    this.append(new Uint8Array(buf));
  }

  /** Concatenate all chunks into a single Uint8Array. */
  finish(): Uint8Array {
    if (this.chunks.length === 0) {
      return this.current.subarray(0, this.offset);
    }
    const total = this.chunks.reduce((sum, c) => sum + c.length, this.offset);
    const out = new Uint8Array(total);
    let pos = 0;
    for (const chunk of this.chunks) {
      out.set(chunk, pos);
      pos += chunk.length;
    }
    out.set(this.current.subarray(0, this.offset), pos);
    return out;
  }

  /** Reuse the buffer for the next nested message (drop chunks, reset offset). */
  reset(): void {
    this.chunks.length = 0;
    this.offset = 0;
  }
}
