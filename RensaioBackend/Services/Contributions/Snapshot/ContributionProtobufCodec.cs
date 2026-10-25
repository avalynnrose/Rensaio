using RensaioBackend.Models.ContributionDatabase;
using System.Text;

namespace RensaioBackend.Services.Contributions.Snapshot
{
    /// <summary>
    /// Minimal protobuf wire reader for the ContributionSnapshotV1 export format.
    /// Mirrors the Cloudflare worker's `proto/codec.ts` field numbers so the backend
    /// can decode the `metadata.bin` payload without a code-generated proto class.
    ///
    /// Field layout (proto/contribution_snapshot_v1.proto):
    ///   ContributionSnapshotV1: 1=schema_version, 2=generated_utc, 3=version,
    ///                            4=titles, 5=mapping_titles, 6=sources, 7=series, 8=metadata
    ///   TitleEntity:             1=id, 2=title, 3=v
    ///   SeriesTitleEntity:       1=series_id, 2=title_id, 3=v
    ///   ContributionSourceEntity:1=id, 2=package, 3=source_id(int64), 4=source_name,
    ///                            5=source_language, 6=last_batch_utc, 7=v
    ///                            8=latest_ids (repeated string)
    ///   ContributionSeriesEntity:1=id, 2=mapping_id, 3=source_id, 4=record, 5=v,
    ///                            6=verified_date_utc (optional),
    ///                            7=metadata (derived bool: has a live Metadata row)
    ///   ContributionRecordV2:    1=schema_version, 2=title_id, 3=thumbnail_url, 4=status,
    ///                            5=seen_in_popular, 6=seen_in_latest, 7=last_chapter(double), 8=last_update_utc,
    ///                            9=url, 10=memo, 11=seen_in_popular/is_latest are retained as-is.
    ///                            (v1 payloads carry schema_version=1 and no url — they decode to a
    ///                             legacy-shaped V2 that the context tombstones.)
    ///   ContributionMetadataEntity:1=id, 2=mapping_id, 3=provider_id, 4=provider_key,
    ///                            5=mapping_status, 6=linked_date, 7=v
    /// </summary>
    internal static class ContributionProtobufCodec
    {
        public static ContributionSnapshotV1 DecodeSnapshot(ReadOnlySpan<byte> data)
        {
            var snapshot = new ContributionSnapshotV1();
            var reader = new ProtoReader(data);
            while (reader.NextField(out int field, out WireType wire))
            {
                switch (field)
                {
                    case 1: snapshot.SchemaVersion = (int)reader.ReadVarint(); break;
                    case 2: snapshot.GeneratedUtc = DateTime.Parse(reader.ReadString(), null, System.Globalization.DateTimeStyles.RoundtripKind); break;
                    case 3: snapshot.Version = (int)reader.ReadVarint(); break;
                    case 4: snapshot.Titles.Add(DecodeTitle(reader.ReadBytes())); break;
                    case 5: snapshot.Mappings.Add(DecodeMappingTitle(reader.ReadBytes())); break;
                    case 6: snapshot.Sources.Add(DecodeSource(reader.ReadBytes())); break;
                    case 7: snapshot.Series.Add(DecodeSeries(reader.ReadBytes())); break;
                    case 8: snapshot.Metadata.Add(DecodeMetadata(reader.ReadBytes())); break;
                    case 9: snapshot.SourceSeries.Add(DecodeSourceSeries(reader.ReadBytes())); break;
                    default: reader.Skip(wire); break;
                }
            }
            return snapshot;
        }

        private static TitleEntity DecodeTitle(ReadOnlySpan<byte> data)
        {
            var title = new TitleEntity();
            var r = new ProtoReader(data);
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: title.Id = Guid.Parse(r.ReadString()); break;
                    case 2: title.Title = r.ReadString(); break;
                    case 3: title.Version = (int)r.ReadVarint(); break;
                    default: r.Skip(w); break;
                }
            }
            return title;
        }

        private static SeriesTitleEntity DecodeMappingTitle(ReadOnlySpan<byte> data)
        {
            var mt = new SeriesTitleEntity();
            var r = new ProtoReader(data);
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: mt.SeriesId = Guid.Parse(r.ReadString()); break;
                    case 2: mt.TitleId = Guid.Parse(r.ReadString()); break;
                    case 3: mt.Version = (int)r.ReadVarint(); break;
                    default: r.Skip(w); break;
                }
            }
            return mt;
        }

        private static ContributionSourceEntity DecodeSource(ReadOnlySpan<byte> data)
        {
            // ContributionSourceEntity identity/display fields are `init`-only →
            // collect into locals first, then construct via object initializer.
            var id = Guid.Empty;
            var package = string.Empty;
            var packageId = Guid.Empty;
            long sourceId = 0;
            var sourceName = string.Empty;
            var sourceLanguage = string.Empty;
            DateTime? lastBatch = null;
            int version = 0;
            var latestIds = new List<string>();

            var r = new ProtoReader(data);
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: id = Guid.Parse(r.ReadString()); break;
                    case 2: package = r.ReadString(); break;
                    case 3: sourceId = r.ReadVarint64(); break;
                    case 4: sourceName = r.ReadString(); break;
                    case 5: sourceLanguage = r.ReadString(); break;
                    case 6: lastBatch = ParseDate(r.ReadString()); break;
                    case 7: version = (int)r.ReadVarint(); break;
                    case 8: latestIds.Add(r.ReadString()); break;
                    case 9: packageId = Guid.Parse(r.ReadString()); break;
                    default: r.Skip(w); break;
                }
            }

            return new ContributionSourceEntity
            {
                Id = id,
                Package = package,
                PackageId = packageId != Guid.Empty ? packageId : ContributionSourceEntity.DerivePackageId(package),
                SourceId = sourceId,
                SourceName = sourceName,
                SourceLanguage = sourceLanguage,
                LastBatchExecutionUTC = lastBatch,
                LatestIds = latestIds,
                Version = version
            };
        }

        private static ContributionSourceSeriesEntity DecodeSourceSeries(ReadOnlySpan<byte> data)
        {
            var ss = new ContributionSourceSeriesEntity();
            var r = new ProtoReader(data);
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: ss.SourceId = Guid.Parse(r.ReadString()); break;
                    case 2: ss.SeriesEntityId = Guid.Parse(r.ReadString()); break;
                    case 3: ss.Version = (int)r.ReadVarint(); break;
                    default: r.Skip(w); break;
                }
            }
            return ss;
        }

        private static ContributionSeriesEntity DecodeSeries(ReadOnlySpan<byte> data)
        {
            var series = new ContributionSeriesEntity();
            var r = new ProtoReader(data);
            var record = new ContributionRecordV2();
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: series.Id = Guid.Parse(r.ReadString()); break;
                    case 2: series.SeriesId = Guid.Parse(r.ReadString()); break;
                    case 3: series.SourceId = Guid.Parse(r.ReadString()); break;
                    case 4:
                        var rec = new ProtoReader(r.ReadBytes());
                        while (rec.NextField(out int rf, out WireType rw))
                        {
                            switch (rf)
                            {
                                case 1: record.SchemaVersion = (int)rec.ReadVarint(); break;
                                case 2: record.TitleId = Guid.Parse(rec.ReadString()); break;
                                case 3: record.ThumbnailUrl = rec.ReadString(); break;
                                case 4: record.Status = (int)rec.ReadVarint(); break;
                                case 5: record.SeenInPopular = rec.ReadVarint() != 0; break;
                                case 6: record.SeenInLatest = rec.ReadVarint() != 0; break;
                                case 7: record.LastChapter = (decimal)rec.ReadFixed64Double(); break;
                                case 8: record.LastUpdateUTC = ParseDate(rec.ReadString()); break;
                                case 9: record.Url = rec.ReadString(); break;
                                case 10: record.Memo = rec.ReadString(); break;
                                default: rec.Skip(rw); break;
                            }
                        }
                        series.Data = record;
                        break;
                    case 5: series.Version = (int)r.ReadVarint(); break;
                    case 6: series.VerifiedDateUTC = ParseDate(r.ReadString()); break;
                    case 7: series.Metadata = r.ReadVarint() != 0; break;
                    case 8: series.PackageId = Guid.Parse(r.ReadString()); break;
                    default: r.Skip(w); break;
                }
            }
            return series;
        }

        private static ContributionMetadataEntity DecodeMetadata(ReadOnlySpan<byte> data)
        {
            var md = new ContributionMetadataEntity();
            var r = new ProtoReader(data);
            while (r.NextField(out int f, out WireType w))
            {
                switch (f)
                {
                    case 1: md.Id = Guid.Parse(r.ReadString()); break;
                    case 2: md.SeriesId = Guid.Parse(r.ReadString()); break;
                    case 3: md.ProviderId = (int)r.ReadVarint(); break;
                    case 4: md.ProviderKey = r.ReadString(); break;
                    case 5: md.MappingStatus = (RensaioBackend.Models.Database.SeriesMappingStatus)(int)r.ReadVarint(); break;
                    case 6: md.LinkedDate = ParseDate(r.ReadString()); break;
                    case 7: md.Version = (int)r.ReadVarint(); break;
                    default: r.Skip(w); break;
                }
            }
            return md;
        }

        private static DateTime? ParseDate(string s) =>
            string.IsNullOrEmpty(s) ? null : DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

        internal enum WireType
        {
            Varint = 0,
            Fixed64 = 1,
            LengthDelimited = 2,
            StartGroup = 3,
            EndGroup = 4,
            Fixed32 = 5
        }

        /// <summary>Forward-only protobuf wire reader over a byte span.</summary>
        private ref struct ProtoReader
        {
            private ReadOnlySpan<byte> _data;
            private int _pos;

            public ProtoReader(ReadOnlySpan<byte> data)
            {
                _data = data;
                _pos = 0;
            }

            public bool NextField(out int field, out WireType wire)
            {
                if (_pos >= _data.Length)
                {
                    field = 0;
                    wire = default;
                    return false;
                }
                ulong key = ReadVarint();
                field = (int)(key >> 3);
                wire = (WireType)(key & 0x7);
                return true;
            }

            public ulong ReadVarint()
            {
                ulong value = 0;
                int shift = 0;
                while (true)
                {
                    if (_pos >= _data.Length) throw new EndOfStreamException("Truncated varint");
                    byte b = _data[_pos++];
                    value |= (ulong)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                    if (shift > 63) throw new InvalidDataException("Varint too long");
                }
                return value;
            }

            public long ReadVarint64() => unchecked((long)ReadVarint());

            public double ReadFixed64Double()
            {
                if (_pos + 8 > _data.Length) throw new EndOfStreamException("Truncated fixed64");
                long bits = BitConverter.ToInt64(_data.Slice(_pos, 8).ToArray(), 0);
                _pos += 8;
                return BitConverter.Int64BitsToDouble(bits);
            }

            public string ReadString()
            {
                int len = checked((int)ReadVarint());
                if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("Invalid string length");
                string s = Encoding.UTF8.GetString(_data.Slice(_pos, len));
                _pos += len;
                return s;
            }

            public ReadOnlySpan<byte> ReadBytes()
            {
                int len = checked((int)ReadVarint());
                if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("Invalid bytes length");
                var span = _data.Slice(_pos, len);
                _pos += len;
                return span;
            }

            public void Skip(WireType wire)
            {
                switch (wire)
                {
                    case WireType.Varint: ReadVarint(); break;
                    case WireType.Fixed64: _pos += 8; break;
                    case WireType.Fixed32: _pos += 4; break;
                    case WireType.LengthDelimited:
                        int len = checked((int)ReadVarint());
                        if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("Invalid length");
                        _pos += len;
                        break;
                    default:
                        throw new InvalidDataException($"Cannot skip wire type {wire}");
                }
            }
        }
    }
}