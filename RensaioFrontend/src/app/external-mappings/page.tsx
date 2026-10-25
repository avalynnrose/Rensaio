"use client";

import React, { useState, useEffect, useCallback } from 'react';
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { LazyImage } from "@/components/ui/lazy-image";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ExternalLink, Search, Ban, RotateCcw, EyeOff, RefreshCw, Clock, Check, AlertCircle, CloudUpload } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { getProgressHub } from '@/lib/api/signalr/progressHub';
import { JobType, ProgressStatus } from '@/lib/api/types';
import { useContributionUpload } from '@/lib/api/hooks/useContributionUpload';
import { useToast } from '@/hooks/use-toast';
import { useContributionEnabled } from '@/hooks/use-contribution-enabled';
import { useExternalMappings, useExternalMappingsScanAll, useExternalMappingsScanSeries, useExternalMappingsBlock, useExternalMappingsUnblock, useExternalMappingsIgnore, useExternalMappingsIgnoreAll } from "@/lib/api/hooks/useExternalMappings";
import { ScrobblerSearchRequester } from '@/components/comp/scrobbler/scrobbler-search-requester';
import { ScrobblerProvider, SeriesMappingStatus, type ExternalSeriesGroup, type ExternalMappingsPage, type ExternalSeriesProviderMapping } from '@/lib/api/types';

type SeriesScanState = 'scanning' | 'done' | 'failed';

// Short badge identifiers for each metadata provider (matches the settings page icon treatment).
/** Thumbnail that shows a larger 3:4 popover on hover (same pattern as the scrobbler search dialogs). */
function ThumbnailPopover({ src, alt, className = 'h-10 w-10 rounded object-cover flex-shrink-0', side = 'right' }: {
  src: string;
  alt?: string;
  className?: string;
  side?: 'right' | 'left' | 'top' | 'bottom';
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <LazyImage src={src} alt={alt ?? ''} className={className} loading="eager" />
      </TooltipTrigger>
      <TooltipPrimitive.Portal>
        <TooltipContent side={side} className="p-0 bg-transparent border-none shadow-none">
          <div className="relative w-48 aspect-[3/4]">
            <LazyImage
              src={src}
              alt={alt ?? ''}
              className="w-full h-full object-cover rounded-md border border-secondary"
            />
          </div>
        </TooltipContent>
      </TooltipPrimitive.Portal>
    </Tooltip>
  );
}

function statusBadge(status: SeriesMappingStatus) {
  switch (status) {
    case SeriesMappingStatus.Unmatched: return <Badge variant="secondary">Not matched</Badge>;
    case SeriesMappingStatus.AutoMatched: return <Badge variant="default">Auto</Badge>;
    case SeriesMappingStatus.UserConfirmed: return <Badge variant="default">User</Badge>;
    case SeriesMappingStatus.ManualIgnore: return <Badge variant="secondary">Manual ignore</Badge>;
    case SeriesMappingStatus.ForeverIgnored: return <Badge variant="secondary">Disabled</Badge>;
    case SeriesMappingStatus.Blocked: return <Badge variant="destructive">Blocked</Badge>;
    default: return <Badge variant="secondary">Unknown</Badge>;
  }
}

export default function ExternalMappingsPage() {
  const contributionEnabled = useContributionEnabled();
  const { toast } = useToast();
  const contributionUpload = useContributionUpload();

  const handleExportToCloud = () => {
    contributionUpload.mutate(undefined, {
      onSuccess: () => {
        toast({
          title: 'Contribution export queued',
          description: 'Your contributions are being uploaded to the cloud in the background.',
        });
      },
      onError: (error) => {
        toast({
          title: 'Contribution export failed',
          description: error instanceof Error ? error.message : 'Unknown error',
          variant: 'destructive',
        });
      },
    });
  };

  const [filter, setFilter] = useState<'all' | 'unmatched' | 'blocked'>('unmatched');
  const [page, setPage] = useState(0);
  const [statusFilter, setStatusFilter] = useState<number | null>(null);
  const [scanProgress, setScanProgress] = useState<string | null>(null);
  const [seriesScanStates, setSeriesScanStates] = useState<Record<string, SeriesScanState>>({});
  const [searchTarget, setSearchTarget] = useState<{
    seriesId: string;
    seriesTitle: string;
    seriesThumbnail?: string;
    seriesAltTitles?: string;
    provider: ScrobblerProvider;
  } | null>(null);
  const pageSize = 50;
  const queryClient = useQueryClient();

  const { data, isLoading: loading } = useExternalMappings({ filter, page, pageSize, status: statusFilter });
  const scanAll = useExternalMappingsScanAll();
  const scanSeries = useExternalMappingsScanSeries();
  const block = useExternalMappingsBlock();
  const unblock = useExternalMappingsUnblock();
  const ignore = useExternalMappingsIgnore();
  const ignoreAll = useExternalMappingsIgnoreAll();

  // Refresh the table after a manual map confirm (external-mappings rows changed).
  const handleSearchConfirmed = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
  }, [queryClient]);

  // Live progress for metadata scans via SignalR. The backend emits two families of events:
  //  - id = "metadata-link-all"            → global scan aggregate (header badge next to Scan now).
  //  - id = "metadata-link-{seriesId}"     → single-series scan (per-row spinner + done/failed badge).
  // The per-series events also fire while a global scan runs (each row lights up as it processes).
  useEffect(() => {
    let unsubscribe: (() => void) | null = null;
    const listen = async () => {
      try {
        await getProgressHub().startConnection();
        unsubscribe = getProgressHub().onProgress((p) => {
          if (p.jobType !== JobType.MetadataLink) return;

          // Global scan aggregate → the header badge near "Scan now".
          if (p.id === 'metadata-link-all') {
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setScanProgress(p.message || `Linking… ${p.percentage ?? 0}%`);
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setScanProgress(p.message || 'Scan complete');
              void queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
              setTimeout(() => setScanProgress(null), 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setScanProgress(p.errorMessage || 'Scan failed');
            }
            return;
          }

          // Single-series scan → per-row feedback on the mapping table.
          const prefix = 'metadata-link-';
          if (p.id?.startsWith(prefix)) {
            const seriesId = p.id.slice(prefix.length);
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setSeriesScanStates((prev) => ({ ...prev, [seriesId]: 'scanning' }));
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setSeriesScanStates((prev) => ({ ...prev, [seriesId]: 'done' }));
              void queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
              setTimeout(() => {
                setSeriesScanStates((prev) => {
                  const next = { ...prev };
                  delete next[seriesId];
                  return next;
                });
              }, 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setSeriesScanStates((prev) => ({ ...prev, [seriesId]: 'failed' }));
            }
          }
        });
      } catch {
        // SignalR unavailable — the per-row spinner still shows via the mutation's isPending,
        // and the table refreshes when the mutation completes.
      }
    };
    void listen();
    return () => { unsubscribe?.(); };
  }, [queryClient]);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">External Mappings</h1>
        <p className="text-sm text-muted-foreground">
          Review and manage metadata links between local series and external providers.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        {/* Filter combo — left aligned, default Unmatched. */}
        <Select value={filter} onValueChange={(v) => { setFilter(v as 'all' | 'unmatched' | 'blocked'); setPage(0); }}>
          <SelectTrigger className="w-40">
            <SelectValue placeholder="Unmatched" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="unmatched">Unmatched</SelectItem>
            <SelectItem value="all">All</SelectItem>
            <SelectItem value="blocked">Blocked</SelectItem>
          </SelectContent>
        </Select>

        <div className="ml-auto flex items-center gap-2">
          {/* Export to cloud — only when the Contributor Id is verified. */}
          {contributionEnabled && (
            <Button
              size="sm"
              variant="outline"
              className="gap-1"
              onClick={handleExportToCloud}
              disabled={contributionUpload.isPending}
            >
              <CloudUpload className={`h-4 w-4 ${contributionUpload.isPending ? 'animate-pulse' : ''}`} />
              {contributionUpload.isPending ? 'Exporting…' : 'Export to cloud'}
            </Button>
          )}

          {/* Scan now — right aligned, icon + primary style (like the library Add Series). */}
          <Button
            size="sm"
            className="gap-1"
            onClick={() => { setScanProgress('Starting scan…'); scanAll.mutate(); }}
            disabled={scanAll.isPending}
          >
            <RefreshCw className={`h-4 w-4 ${scanAll.isPending ? 'animate-spin' : ''}`} />
            {scanAll.isPending ? 'Scanning…' : 'Scan now'}
          </Button>
          {scanProgress && (
            <Badge variant="secondary">{scanProgress}</Badge>
          )}
        </div>
      </div>

      <SeriesTable
        groups={data?.series ?? []}
        providerMeta={data?.providerMeta ?? {}}
        loading={loading}
        scanSeries={scanSeries}
        block={block}
        unblock={unblock}
        ignore={ignore}
        ignoreAll={ignoreAll}
        seriesScanStates={seriesScanStates}
        onSearch={setSearchTarget}
      />

      {searchTarget && (
        <ScrobblerSearchRequester
          open
          onOpenChange={(open) => {
            if (!open) {
              setSearchTarget(null);
              handleSearchConfirmed();
            }
          }}
          provider={searchTarget.provider}
          seriesId={searchTarget.seriesId}
          seriesTitle={searchTarget.seriesTitle}
          seriesThumbnail={searchTarget.seriesThumbnail}
          seriesAltTitles={searchTarget.seriesAltTitles}
        />
      )}

      {/* Pagination */}
      <div className="flex items-center gap-2">
        <Button variant="outline" size="sm" disabled={page <= 0} onClick={() => setPage(page - 1)}>Prev</Button>
        <span className="text-sm">Page {page + 1} · {data?.total ?? 0} total</span>
        <Button
          variant="outline"
          size="sm"
          disabled={(data?.total ?? 0) <= (page + 1) * pageSize}
          onClick={() => setPage(page + 1)}
        >
          Next
        </Button>
      </div>
    </div>
  );
}

interface SeriesTableActions {
  scanSeries: ReturnType<typeof useExternalMappingsScanSeries>;
  block: ReturnType<typeof useExternalMappingsBlock>;
  unblock: ReturnType<typeof useExternalMappingsUnblock>;
  ignore: ReturnType<typeof useExternalMappingsIgnore>;
  ignoreAll: ReturnType<typeof useExternalMappingsIgnoreAll>;
  seriesScanStates?: Record<string, SeriesScanState>;
  onSearch: (target: {
    seriesId: string;
    seriesTitle: string;
    seriesThumbnail?: string;
    seriesAltTitles?: string;
    provider: ScrobblerProvider;
  }) => void;
}

function SeriesTable({ groups, providerMeta, loading, scanSeries, block, unblock, ignore, ignoreAll, onSearch, seriesScanStates }: {
  groups: ExternalSeriesGroup[];
  providerMeta: ExternalMappingsPage['providerMeta'];
  loading: boolean;
} & SeriesTableActions) {
  if (loading) return <p className="text-sm text-muted-foreground">Loading…</p>;
  if (groups.length === 0) return <p className="text-sm text-muted-foreground">No mappings yet. Run a scan.</p>;

  return (
    <TooltipProvider>
      <div className="rounded-md border">
        <table className="w-full text-sm">
        <tbody>
          {groups.map((g) => (
            <SeriesGroupRows
              key={g.seriesId}
              group={g}
              providerMeta={providerMeta}
              scanSeries={scanSeries}
              block={block}
              unblock={unblock}
              ignore={ignore}
              ignoreAll={ignoreAll}
              seriesScanStates={seriesScanStates}
              onSearch={onSearch}
            />
          ))}
        </tbody>
        </table>
      </div>
    </TooltipProvider>
  );
}

/** Renders the grouped series: title header (full width, with group-level Scan) + a left series thumb spanning all provider rows. */
function SeriesGroupRows({ group, providerMeta, scanSeries, block, unblock, ignore, ignoreAll, onSearch, seriesScanStates }: {
  group: ExternalSeriesGroup;
  providerMeta: ExternalMappingsPage['providerMeta'];
} & SeriesTableActions) {
  const providers = group.providers ?? [];
  const span = Math.max(providers.length, 1);
  const scanState = seriesScanStates?.[group.seriesId];
  const isScanning = scanState === 'scanning';
  return (
    <>
      <tr className="border-b bg-muted last:border-0">
        <td colSpan={7} className="py-2 pl-4 pr-3">
          <div className="flex items-center gap-2">
            <span className="font-semibold">{group.seriesTitle || group.seriesId}</span>
            <Button
              variant="outline"
              size="sm"
              className="ml-auto gap-1"
              onClick={() => ignoreAll.mutate(group.seriesId)}
              disabled={ignoreAll.isPending}
              title="Mark every Not matched provider for this series as Ignore always"
            >
              <EyeOff className="h-3.5 w-3.5" />
              {ignoreAll.isPending ? 'Ignoring…' : 'Ignore All'}
            </Button>
            <Button
              variant="outline"
              size="sm"
              className="gap-1"
              onClick={() => scanSeries.mutate(group.seriesId)}
              disabled={isScanning}
              title={
                scanState === 'done'
                  ? 'Scan complete'
                  : scanState === 'failed'
                    ? 'Scan failed'
                    : isScanning
                      ? `Scanning ${group.seriesTitle || 'series'}…`
                      : `Scan ${group.seriesTitle || 'series'} against providers`
              }
            >
              {scanState === 'done' ? (
                <Check className="h-3.5 w-3.5" />
              ) : scanState === 'failed' ? (
                <AlertCircle className="h-3.5 w-3.5" />
              ) : (
                <RotateCcw className={`h-3.5 w-3.5 ${isScanning ? 'animate-spin' : ''}`} />
              )}
              {scanState === 'done'
                ? 'Done'
                : scanState === 'failed'
                  ? 'Failed'
                  : isScanning
                    ? 'Scanning…'
                    : 'Scan'}
            </Button>
          </div>
        </td>
      </tr>
      {providers.map((m, i) => (
        <tr key={`${group.seriesId}-${m.provider}`} className="border-b last:border-0">
          {i === 0 && (
            <td rowSpan={span} className="py-2 align-top pl-3 pr-1">
              {group.seriesCoverUrl && (
                <ThumbnailPopover src={group.seriesCoverUrl} alt={group.seriesTitle} className="w-36 rounded object-cover flex-shrink-0" />
              )}
            </td>
          )}
          <td className="py-2 pl-1">
            {(() => {
              const meta = providerMeta?.[ScrobblerProvider[m.provider]];
              return (
                <div className="flex items-center gap-2">
                  {meta?.icon && (
                    <img src={meta.icon} alt="" className="h-5 w-5 rounded-full flex-shrink-0" />
                  )}
                  <span>{ScrobblerProvider[m.provider]}</span>
                </div>
              );
            })()}
          </td>
          <td className="py-2 px-2">
            {m.providerCoverUrl ? (
              <ThumbnailPopover src={m.providerCoverUrl} alt={m.externalSeriesTitle} className="h-14 w-10 rounded object-cover flex-shrink-0" />
            ) : '—'}
          </td>
          <td className="py-2 pr-3">
            <div className="flex flex-col">
              <span>{m.externalSeriesTitle || '—'}</span>
              {(() => {
                const id = m.externalSeriesId;
                const url = providerMeta?.[ScrobblerProvider[m.provider]]?.seriesUrlTemplate;
                if (!id || !url) return <span className="text-xs text-muted-foreground">{id || '—'}</span>;
                return (
                  <a
                    href={url.replace('{0}', encodeURIComponent(id))}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
                    title={`Open ${ScrobblerProvider[m.provider]} page`}
                  >
                    <ExternalLink className="h-3 w-3 flex-shrink-0" />
                    {id}
                  </a>
                );
              })()}
            </div>
          </td>
          <td className="py-2 pr-3">
            {(() => {
              const alt = m.alternativeTitles ?? [];
              if (alt.length === 0) return '—';
              return (
                <span className="text-xs text-muted-foreground break-words max-w-[280px]">{alt.join(' · ')}</span>
              );
            })()}
          </td>
          <td className="py-2 pr-3">
            <div className="flex flex-col gap-0.5">
              {statusBadge(m.mappingStatus)}
              {m.mappingStatus === SeriesMappingStatus.ManualIgnore && m.linkedDate && (
                <span className="text-xs text-muted-foreground">
                  Triggered {new Date(m.linkedDate).toLocaleDateString()}
                </span>
              )}
            </div>
          </td>
          <td className="py-2 pr-3">
            <div className="flex gap-1">
              <Button
                variant="outline"
                size="sm"
                className="gap-1"
                onClick={() => onSearch({
                  seriesId: group.seriesId,
                  seriesTitle: group.seriesTitle || group.seriesId,
                  seriesThumbnail: group.seriesCoverUrl,
                  seriesAltTitles: m.alternativeTitles?.join(' · ') ?? undefined,
                  provider: m.provider,
                })}
                title={`Search ${ScrobblerProvider[m.provider]} for a match`}
              >
                <Search className="h-3.5 w-3.5" />
                Search
              </Button>
              {m.mappingStatus === SeriesMappingStatus.Blocked ? (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => unblock.mutate({ seriesId: group.seriesId, provider: m.provider.toString() })}
                  title="Unblock this provider"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Unblock
                </Button>
              ) : (
                (m.mappingStatus === SeriesMappingStatus.AutoMatched || m.mappingStatus === SeriesMappingStatus.UserConfirmed) && (
                  <Button
                    variant="outline"
                    size="sm"
                    className="gap-1"
                    onClick={() => block.mutate({ seriesId: group.seriesId, provider: m.provider.toString() })}
                    title="Block this provider link"
                  >
                    <Ban className="h-3.5 w-3.5" />
                    Block
                  </Button>
                )
              )}
              {/* Manual ignore (no expiration) */}
              {m.mappingStatus !== SeriesMappingStatus.ManualIgnore && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => ignore.mutate({ seriesId: group.seriesId, provider: m.provider.toString(), forever: false })}
                  title="Manually ignore this provider (no expiration)"
                >
                  <EyeOff className="h-3.5 w-3.5" />
                  Manual Ignore
                </Button>
              )}

              {/* Permanent ignore */}
              {m.mappingStatus !== SeriesMappingStatus.ForeverIgnored && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => ignore.mutate({ seriesId: group.seriesId, provider: m.provider.toString(), forever: true })}
                  title="Ignore this provider forever"
                >
                  <Clock className="h-3.5 w-3.5" />
                  Ignore always
                </Button>
              )}

              {/* Un-ignore: only when the row is currently ignored (manual or forever). */}
              {(m.mappingStatus === SeriesMappingStatus.ManualIgnore || m.mappingStatus === SeriesMappingStatus.ForeverIgnored) && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => unblock.mutate({ seriesId: group.seriesId, provider: m.provider.toString() })}
                  title="Stop ignoring this provider"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Un-ignore
                </Button>
              )}
            </div>
          </td>
        </tr>
      ))}
    </>
  );
}