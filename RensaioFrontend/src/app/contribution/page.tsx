"use client";

import React, { useState, useEffect, useCallback } from 'react';
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { LazyImage } from "@/components/ui/lazy-image";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogClose } from "@/components/ui/dialog";
import { ExternalLink, Search, Ban, RotateCcw, EyeOff, RefreshCw, Clock, Check, AlertCircle, Layers, ShieldAlert, ShieldCheck, ShieldX, CloudUpload, GitMerge, Scissors } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { getProgressHub } from '@/lib/api/signalr/progressHub';
import { JobType, ProgressStatus } from '@/lib/api/types';
import { useContributionUpload } from '@/lib/api/hooks/useContributionUpload';
import { useDebounce } from '@/lib/hooks/useDebounce';
import { useToast } from '@/hooks/use-toast';
import {
  useContributionMappings,
  useContributionMappingsScanAll,
  useContributionMappingsScanMapping,
  useContributionMappingsLink,
  useContributionMappingsBlock,
  useContributionMappingsUnblock,
  useContributionMappingsIgnore,
  useContributionMappingsIgnoreAll,
  useContributionMappingsJoin,
  useContributionMappingsSplit,
  useContributionMappingsVerify,
  useContributionMappingsUnverify,
  useContributionMappingsSmartSearch,
  contributionMappingsQueryKey,
} from "@/lib/api/hooks/useContributionMappings";
import { ScrobblerSearchRequester } from '@/components/comp/scrobbler/scrobbler-search-requester';
import { ScrobblerProvider, SeriesMappingStatus, type ContributionMappingGroup, type ContributionMappingsPage, type ExternalSeriesProviderMapping } from '@/lib/api/types';
import { useContributionEnabled } from '@/hooks/use-contribution-enabled';

type MappingScanState = 'scanning' | 'done' | 'failed';

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

export default function ContributionPage() {
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

  const [filter, setFilter] = useState<'all' | 'unmatched' | 'blocked' | 'manualignored'>('unmatched');
  const [verifiedFilter, setVerifiedFilter] = useState<'all' | 'verified' | 'unverified'>('unverified');
  const [nameInput, setNameInput] = useState('');
  const debouncedName = useDebounce(nameInput, 300);
  const [page, setPage] = useState(0);
  const [statusFilter, setStatusFilter] = useState<number | null>(null);
  const [scanProgress, setScanProgress] = useState<string | null>(null);
  const [mappingScanStates, setMappingScanStates] = useState<Record<string, MappingScanState>>({});
  // Join selection (mapping ids) + the confirmation dialogs for the join/split destructive ops.
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [joinOpen, setJoinOpen] = useState(false);
  const [splitTarget, setSplitTarget] = useState<ContributionMappingGroup | null>(null);
  const [searchTarget, setSearchTarget] = useState<{
    mappingId: string;
    mappingTitle: string;
    mappingCoverUrl?: string;
    mappingTitles?: string;
    candidateTitles?: string[];
    provider: ScrobblerProvider;
  } | null>(null);
  const pageSize = 10;
  const queryClient = useQueryClient();

  const { data, isLoading: loading } = useContributionMappings({ filter, verified: verifiedFilter, page, pageSize, status: statusFilter, name: debouncedName });
  const scanAll = useContributionMappingsScanAll();
  const scanMapping = useContributionMappingsScanMapping();
  const linkMapping = useContributionMappingsLink();
  const block = useContributionMappingsBlock();
  const unblock = useContributionMappingsUnblock();
  const ignore = useContributionMappingsIgnore();
  const ignoreAll = useContributionMappingsIgnoreAll();
  const joinMappings = useContributionMappingsJoin();
  const splitMapping = useContributionMappingsSplit();
  const verifyMapping = useContributionMappingsVerify();
  const unverifyMapping = useContributionMappingsUnverify();
  const smartSearchMapping = useContributionMappingsSmartSearch();

  // Confirm into the contribution DB via the generic search dialog's onConfirm hook.
  const handleSearchConfirmed = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
  }, [queryClient]);

  // Live progress for contribution mapping scans via SignalR.
  //  - id = "contribution-link-all"            → global scan aggregate (header badge next to Scan now).
  //  - id = "contribution-link-{mappingId}"    → single-mapping scan (per-row spinner + done/failed badge).
  useEffect(() => {
    let unsubscribe: (() => void) | null = null;
    const listen = async () => {
      try {
        await getProgressHub().startConnection();
        unsubscribe = getProgressHub().onProgress((p) => {
          if (p.jobType !== JobType.MetadataLink) return;

          if (p.id === 'contribution-link-all') {
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setScanProgress(p.message || `Linking… ${p.percentage ?? 0}%`);
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setScanProgress(p.message || 'Scan complete');
              void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
              setTimeout(() => setScanProgress(null), 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setScanProgress(p.errorMessage || 'Scan failed');
            }
            return;
          }

          const prefix = 'contribution-link-';
          if (p.id?.startsWith(prefix)) {
            const mappingId = p.id.slice(prefix.length);
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'scanning' }));
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'done' }));
              void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
              setTimeout(() => {
                setMappingScanStates((prev) => {
                  const next = { ...prev };
                  delete next[mappingId];
                  return next;
                });
              }, 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'failed' }));
            }
          }
        });
      } catch {
        // SignalR unavailable — the per-row spinner still shows via the mutation's isPending.
      }
    };
    void listen();
    return () => { unsubscribe?.(); };
  }, [queryClient]);

  // A new filter/name invalidates the current checkbox selection (selection is kept
  // across pages so contributions from different pages can be joined together).
  useEffect(() => {
    setSelectedIds(new Set());
  }, [filter, verifiedFilter, debouncedName]);

  const toggleSelect = useCallback((mappingId: string, checked: boolean) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (checked) next.add(mappingId); else next.delete(mappingId);
      return next;
    });
  }, []);

  const handleJoinConfirmed = useCallback(() => {
    const ids = Array.from(selectedIds);
    joinMappings.mutate(ids, {
      onSuccess: () => {
        setJoinOpen(false);
        setSelectedIds(new Set());
        toast({
          title: 'Contributions joined',
          description: `${ids.length} contributions were merged into one series.`,
        });
      },
      onError: (error) => {
        toast({
          title: 'Join failed',
          description: error instanceof Error ? error.message : 'Unknown error',
          variant: 'destructive',
        });
      },
    });
  }, [selectedIds, joinMappings, toast]);

  const handleSplitConfirmed = useCallback(() => {
    if (!splitTarget) return;
    splitMapping.mutate(splitTarget.mappingId, {
      onSuccess: () => {
        toast({
          title: 'Contribution split',
          description: 'Each source entry is now its own series. Re-match its providers manually.',
        });
        setSplitTarget(null);
      },
      onError: (error) => {
        toast({
          title: 'Split failed',
          description: error instanceof Error ? error.message : 'Unknown error',
          variant: 'destructive',
        });
      },
    });
  }, [splitTarget, splitMapping, toast]);

  // Page-level gate: the Contribution page is only usable once the Contributor
  // Id has been verified against RensaioContributionDB.CF. Until then, show a
  // notice instead of any mapping/export UI.
  if (!contributionEnabled) {
    return (
      <div className="space-y-6">
        <div>
          <p className="flex items-center gap-2 text-sm font-medium">
            <ShieldAlert className="h-4 w-4" />
            Contribution is locked
          </p>
          <p className="mt-2 text-sm text-muted-foreground">
            This page requires a verified Contributor Id. Enable Contribution in
            Settings, enter your Contributor UUID, and verify it against the
            contribution database to unlock contribution uploads and mapping.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">Contribution</h1>
        <p className="text-sm text-muted-foreground">
          Manage sources and their metadata links in the contribution database.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        {/* Global name filter — ANDed with the status combo below. */}
        <Input
          value={nameInput}
          onChange={(e) => { setNameInput(e.target.value); setPage(0); }}
          placeholder="Filter by name…"
          className="h-9 w-56"
        />

        {/* Filter combo — left aligned, default Unmatched. */}
        <Select value={filter} onValueChange={(v) => { setFilter(v as 'all' | 'unmatched' | 'blocked' | 'manualignored'); setPage(0); }}>
          <SelectTrigger className="w-40">
            <SelectValue placeholder="Unmatched" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="unmatched">Unmatched</SelectItem>
            <SelectItem value="all">All</SelectItem>
            <SelectItem value="blocked">Blocked</SelectItem>
            <SelectItem value="manualignored">ManualIgnored</SelectItem>
          </SelectContent>
        </Select>

        {/* Verified combo — next to the status filter, default Unverified. */}
        <Select value={verifiedFilter} onValueChange={(v) => { setVerifiedFilter(v as 'all' | 'verified' | 'unverified'); setPage(0); }}>
          <SelectTrigger className="w-40">
            <SelectValue placeholder="Unverified" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="unverified">Unverified</SelectItem>
            <SelectItem value="verified">Verified</SelectItem>
            <SelectItem value="all">All</SelectItem>
          </SelectContent>
        </Select>

        <div className="ml-auto flex items-center gap-2">
          {/* Join — appears when more than one contribution is selected. */}
          {selectedIds.size > 1 && (
            <Button
              size="sm"
              variant="outline"
              className="gap-1"
              onClick={() => setJoinOpen(true)}
            >
              <GitMerge className="h-4 w-4" />
              Join ({selectedIds.size})
            </Button>
          )}

          {/* Export to cloud — only when the Contributor Id is verified. */}
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

          {/* Scan now — right aligned, icon + primary style. */}
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

      <MappingTable
        groups={data?.groups ?? []}
        providerMeta={data?.providerMeta ?? {}}
        loading={loading}
        scanMapping={scanMapping}
        block={block}
        unblock={unblock}
        ignore={ignore}
        ignoreAll={ignoreAll}
        verify={verifyMapping}
        unverify={unverifyMapping}
        mappingScanStates={mappingScanStates}
        onSearch={setSearchTarget}
        selectedIds={selectedIds}
        onToggleSelect={toggleSelect}
        onSplit={setSplitTarget}
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
          seriesId={searchTarget.mappingId}
          seriesTitle={searchTarget.mappingTitle}
          seriesThumbnail={searchTarget.mappingCoverUrl}
          seriesAltTitles={searchTarget.mappingTitles}
          candidateTitles={searchTarget.candidateTitles}
          onSmartSearch={async () => {
            const res = await smartSearchMapping.mutateAsync({
              mappingId: searchTarget.mappingId,
              provider: searchTarget.provider,
            });
            return res.results.map((h) => ({
              externalId: h.externalId,
              title: h.title,
              alternateTitles: h.alternateTitles ?? [],
              coverUrl: h.coverUrl,
              type: h.type,
              year: h.year,
            }));
          }}
          confirmLabel="Confirm Mapping"
          onConfirm={async (externalSeriesId, externalSeriesTitle) => {
            await linkMapping.mutateAsync({
              mappingId: searchTarget.mappingId,
              provider: searchTarget.provider,
              externalSeriesId,
              externalSeriesTitle,
            });
          }}
        />
      )}

      {/* Join confirmation — destructive merge of the selected contributions. */}
      <Dialog open={joinOpen} onOpenChange={setJoinOpen}>
        <DialogContent className="sm:max-w-[440px]">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <GitMerge className="h-4 w-4" />
              Join {selectedIds.size} contributions
            </DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            The selected contributions will be merged into a single series: their sources, titles
            and metadata links are combined (duplicates removed) under the first selected series.
            This cannot be undone automatically.
          </p>
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="outline" disabled={joinMappings.isPending}>Cancel</Button>
            </DialogClose>
            <Button onClick={handleJoinConfirmed} disabled={joinMappings.isPending}>
              {joinMappings.isPending ? 'Joining…' : 'Join'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Split confirmation — destructive split of a multi-source contribution. */}
      <Dialog open={!!splitTarget} onOpenChange={(open) => { if (!open) setSplitTarget(null); }}>
        <DialogContent className="sm:max-w-[440px]">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Scissors className="h-4 w-4" />
              Split {splitTarget?.displayTitle || 'contribution'}
            </DialogTitle>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            This contribution has {splitTarget?.sources?.length ?? 0} sources. It will be split into
            one series per source: every title and metadata link is cleared, and each new series
            starts with its own source title only. You will need to re-match the metadata providers
            of the new series manually.
          </p>
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="outline" disabled={splitMapping.isPending}>Cancel</Button>
            </DialogClose>
            <Button onClick={handleSplitConfirmed} disabled={splitMapping.isPending}>
              {splitMapping.isPending ? 'Splitting…' : 'Split'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

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

interface MappingTableActions {
  scanMapping: ReturnType<typeof useContributionMappingsScanMapping>;
  block: ReturnType<typeof useContributionMappingsBlock>;
  unblock: ReturnType<typeof useContributionMappingsUnblock>;
  ignore: ReturnType<typeof useContributionMappingsIgnore>;
  ignoreAll: ReturnType<typeof useContributionMappingsIgnoreAll>;
  verify: ReturnType<typeof useContributionMappingsVerify>;
  unverify: ReturnType<typeof useContributionMappingsUnverify>;
  mappingScanStates?: Record<string, MappingScanState>;
  onSearch: (target: {
    mappingId: string;
    mappingTitle: string;
    mappingCoverUrl?: string;
    mappingTitles?: string;
    candidateTitles?: string[];
    provider: ScrobblerProvider;
  }) => void;
  /** Join selection: the set of currently-checked mapping ids. */
  selectedIds: Set<string>;
  onToggleSelect: (mappingId: string, checked: boolean) => void;
  /** Opens the split confirmation for a multi-source contribution. */
  onSplit: (group: ContributionMappingGroup) => void;
}

function MappingTable({ groups, providerMeta, loading, scanMapping, block, unblock, ignore, ignoreAll, verify, unverify, onSearch, mappingScanStates, selectedIds, onToggleSelect, onSplit }: {
  groups: ContributionMappingGroup[];
  providerMeta: ContributionMappingsPage['providerMeta'];
  loading: boolean;
} & MappingTableActions) {
  if (loading) return <p className="text-sm text-muted-foreground">Loading…</p>;
  if (groups.length === 0) return <p className="text-sm text-muted-foreground">No mappings yet. Run a scan.</p>;

  return (
    <TooltipProvider>
      <div className="rounded-md border">
        <table className="w-full text-sm">
        <tbody>
          {groups.map((g) => (
            <MappingGroupRows
              key={g.mappingId}
              group={g}
              providerMeta={providerMeta}
              scanMapping={scanMapping}
              block={block}
              unblock={unblock}
              ignore={ignore}
              ignoreAll={ignoreAll}
              verify={verify}
              unverify={unverify}
              mappingScanStates={mappingScanStates}
              onSearch={onSearch}
              selectedIds={selectedIds}
              onToggleSelect={onToggleSelect}
              onSplit={onSplit}
            />
          ))}
        </tbody>
        </table>
      </div>
    </TooltipProvider>
  );
}

/** Renders the grouped mapping: title header (with member-source summary + group-level Scan) + a left cover spanning all provider rows. */
function MappingGroupRows({ group, providerMeta, scanMapping, block, unblock, ignore, ignoreAll, verify, unverify, onSearch, mappingScanStates, selectedIds, onToggleSelect, onSplit }: {
  group: ContributionMappingGroup;
  providerMeta: ContributionMappingsPage['providerMeta'];
} & MappingTableActions) {
  // Providers ordered by display name inside the card (stable, alphabetical).
  const providers = [...(group.providers ?? [])].sort((a, b) =>
    (ScrobblerProvider[a.provider] ?? '').localeCompare(ScrobblerProvider[b.provider] ?? ''));
  const span = Math.max(providers.length, 1);
  const scanState = mappingScanStates?.[group.mappingId];
  const isScanning = scanState === 'scanning';
  const isSelected = selectedIds.has(group.mappingId);
  const canSplit = (group.sources?.length ?? 0) > 1;
  // Titles (line 1): the MAIN title must always be `displayTitle` (bold), with every other
  // title listed after it. The backend's `titles` array is an unordered automerge set, so it
  // cannot be rendered as-is — its first entry is not necessarily the display title.
  const rawTitles = (group.titles ?? []).map(t => t?.trim()).filter((t): t is string => !!t);
  const displayTitle = group.displayTitle?.trim();
  const displayTitles = displayTitle
    ? [displayTitle, ...rawTitles.filter(t => t.toLowerCase() !== displayTitle.toLowerCase())]
    : rawTitles;
  // Second line under the title: one entry per member source. When the source record carries a
  // canonical page url the entry becomes a hyperlink that opens the series page on that source.
  const sourceSummary = (group.sources ?? [])
    .map(s => ({
      label: s.sourceName || s.sourceKey,
      url: s.sourceUrl,
    }))
    .filter(s => !!s.label);
  return (
    <>
      <tr className="border-b bg-muted last:border-0">
        <td colSpan={7} className="py-2 pl-4 pr-3">
          <div className="flex items-center gap-2">
            <Checkbox
              checked={isSelected}
              onCheckedChange={(checked) => onToggleSelect(group.mappingId, checked)}
              className="shrink-0"
            />
            <Layers className="h-4 w-4 shrink-0 text-muted-foreground" />
            <div className="min-w-0">
              {displayTitles.length > 0 && (
                <div className="truncate max-w-[520px]">
                  {displayTitles.map((t, i) => (
                    <React.Fragment key={`${t}-${i}`}>
                      {i > 0 && <span className="text-muted-foreground"> · </span>}
                      <span className={i === 0 ? 'font-semibold' : 'text-muted-foreground'}>{t}</span>
                    </React.Fragment>
                  ))}
                </div>
              )}
              {sourceSummary.length > 0 && (
                <div className="text-xs text-muted-foreground truncate max-w-[520px]">
                  {sourceSummary.map((s, i) => (
                    <React.Fragment key={`${s.label}-${i}`}>
                      {i > 0 && <span className="text-muted-foreground"> · </span>}
                      {s.url ? (
                        <a
                          href={s.url}
                          target="_blank"
                          rel="noopener noreferrer"
                          className="inline-flex items-center gap-0.5 text-primary hover:underline"
                          title={`Open ${s.label} page`}
                        >
                          {s.label}
                          <ExternalLink className="h-3 w-3 flex-shrink-0" />
                        </a>
                      ) : (
                        <span>{s.label}</span>
                      )}
                    </React.Fragment>
                  ))}
                </div>
              )}
            </div>
            <Button
              variant="outline"
              size="sm"
              className="ml-auto gap-1 shrink-0"
              onClick={() => ignoreAll.mutate(group.mappingId)}
              disabled={ignoreAll.isPending}
              title="Manually ignore every Not matched provider for this mapping"
            >
              <EyeOff className="h-3.5 w-3.5" />
              {ignoreAll.isPending ? 'Ignoring…' : 'Ignore All'}
            </Button>
            {canSplit && (
              <Button
                variant="outline"
                size="sm"
                className="gap-1 shrink-0"
                onClick={() => onSplit(group)}
                title={`Split this contribution's ${group.sources.length} sources into separate series`}
              >
                <Scissors className="h-3.5 w-3.5" />
                Split
              </Button>
            )}
            <Button
              variant="outline"
              size="sm"
              className="gap-1 shrink-0"
              onClick={() => scanMapping.mutate(group.mappingId)}
              disabled={isScanning}
              title={
                scanState === 'done'
                  ? 'Scan complete'
                  : scanState === 'failed'
                    ? 'Scan failed'
                    : isScanning
                      ? `Scanning ${group.displayTitle || 'mapping'}…`
                      : `Scan ${group.displayTitle || 'mapping'} against providers`
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
            {/* Verify / Un-Verify — after Ignore All and Scan. When already verified the
                verification date is shown BEFORE the Un-Verify button. */}
            {group.verifiedDateUtc ? (
              <>
                <span className="text-xs text-muted-foreground shrink-0">
                  Verified {new Date(group.verifiedDateUtc).toLocaleDateString()}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1 shrink-0"
                  onClick={() => unverify.mutate(group.mappingId)}
                  disabled={unverify.isPending}
                  title="Clear the verification of every source entry of this contribution"
                >
                  <ShieldX className="h-3.5 w-3.5" />
                  {unverify.isPending ? 'Un-verifying…' : 'Un-Verify'}
                </Button>
              </>
            ) : (
              <Button
                variant="destructive"
                size="sm"
                className="gap-1 shrink-0"
                onClick={() => verify.mutate(group.mappingId)}
                disabled={verify.isPending}
                title="Mark every source entry of this contribution as verified"
              >
                <ShieldCheck className="h-3.5 w-3.5" />
                {verify.isPending ? 'Verifying…' : 'Verify'}
              </Button>
            )}
          </div>
        </td>
      </tr>
      {providers.map((m, i) => (
        <tr key={`${group.mappingId}-${m.provider}`} className="border-b last:border-0">
          {i === 0 && (
            <td rowSpan={span} className="py-2 align-top pl-3 pr-1">
              {group.coverUrl ? (
                <ThumbnailPopover src={group.coverUrl} alt={group.displayTitle} className="w-36 rounded object-cover flex-shrink-0" />
              ) : (
                <div className="w-36 h-48 rounded bg-muted flex items-center justify-center text-xs text-muted-foreground">No cover</div>
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
                  mappingId: group.mappingId,
                  mappingTitle: group.displayTitle || group.mappingId,
                  mappingCoverUrl: group.coverUrl,
                  mappingTitles: group.titles?.join(' · ') ?? undefined,
                  candidateTitles: group.titles,
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
                  onClick={() => unblock.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), key: m.externalSeriesId })}
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
                    onClick={() => block.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), key: m.externalSeriesId })}
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
                  onClick={() => ignore.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), forever: false })}
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
                  onClick={() => ignore.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), forever: true })}
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
                  onClick={() => unblock.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), key: m.externalSeriesId })}
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
