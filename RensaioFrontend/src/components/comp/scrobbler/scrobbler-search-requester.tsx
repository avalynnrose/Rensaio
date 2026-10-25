"use client";

import React, { useState, useCallback, useRef, useEffect } from 'react';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { ComboInput } from "@/components/ui/combo-input";
import { useSearchExternal, useConfirmMatch, useScrobblerConfigs } from '@/lib/api/hooks/useScrobbler';
import { useToast } from '@/hooks/use-toast';
import { ScrobblerProvider, type ScrobblerSearchResult } from '@/lib/api/types';
import { Search, Check, Sparkles } from 'lucide-react';
import { LazyImage } from "@/components/ui/lazy-image";
import { formatThumbnailUrl } from "@/lib/utils/thumbnail";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "@/components/ui/tooltip";

interface ScrobblerSearchRequesterProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  provider: ScrobblerProvider;
  seriesId: string;
  seriesTitle: string;
  seriesThumbnail?: string;
  seriesAltTitles?: string;
  /**
   * Optional confirm callback. When provided it replaces the default Rensaio series-scoped
   * confirm (`useConfirmMatch`), letting other pages (e.g. Contribution Mappings) confirm into
   * their own persistence layer while reusing the exact same search dialog UI.
   */
  onConfirm?: (externalSeriesId: string, externalSeriesTitle?: string) => Promise<void> | void;
  confirmLabel?: string;
  /**
   * Extra titles to offer as suggestions in the search box (the combobox lists every possible
   * title so the user can pick one instead of typing). The series title is always included.
   */
  candidateTitles?: string[];
  /**
   * Optional Smart Search loader. When provided, a "Smart Search" button appears next to the
   * normal Search button: clicking it calls this loader and replaces the results grid with the
   * ranked candidates (no textbox query is used). Omitted by callers without a smart backend,
   * in which case the button is hidden.
   */
  onSmartSearch?: () => Promise<ScrobblerSearchResult[]>;
}

export function ScrobblerSearchRequester({
  open,
  onOpenChange,
  provider,
  seriesId,
  seriesTitle,
  seriesThumbnail,
  seriesAltTitles,
  onConfirm,
  confirmLabel = 'Confirm Selection',
  candidateTitles,
  onSmartSearch,
}: ScrobblerSearchRequesterProps) {
  const searchExternal = useSearchExternal();
  const confirmMatch = useConfirmMatch();
  const { toast } = useToast();
  const { data: configs } = useScrobblerConfigs();
  const imageTemplateUrl = configs?.find(c => c.provider === provider)?.imageTemplateUrl;

  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<ScrobblerSearchResult[]>([]);
  const [isSearching, setIsSearching] = useState(false);
  const [isSmartSearching, setIsSmartSearching] = useState(false);
  const [hasSearched, setHasSearched] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [isConfirming, setIsConfirming] = useState(false);
  const searchInputRef = useRef<HTMLInputElement>(null);

  const handleSearch = useCallback(async () => {
    if (!searchQuery.trim()) return;
    setIsSearching(true);
    setSelectedId(null);
    setHasSearched(false);
    try {
      const result = await searchExternal.mutateAsync({ provider, query: searchQuery });
      setSearchResults(result.results);
      setHasSearched(true);
    } catch (error) {
      console.error('Failed to search:', error);
      toast({
        title: 'Search failed',
        description: error instanceof Error ? error.message : 'Could not reach the provider. Please try again.',
        variant: 'destructive',
      });
    } finally {
      setIsSearching(false);
    }
  }, [searchQuery, provider, searchExternal, toast]);

  // Smart Search: explicit click handler (no auto-load on open). Searches the provider with every
  // series title + the existing covers via the caller's loader and replaces the results grid.
  const handleSmartSearch = useCallback(async () => {
    if (!onSmartSearch) return;
    setSelectedId(null);
    setHasSearched(false);
    setIsSmartSearching(true);
    try {
      const results = await onSmartSearch();
      setSearchResults(results);
      setHasSearched(true);
    } catch (error) {
      console.error('Smart search failed:', error);
      toast({
        title: 'Smart search failed',
        description: error instanceof Error ? error.message : 'Could not reach the provider. Please try again.',
        variant: 'destructive',
      });
    } finally {
      setIsSmartSearching(false);
    }
  }, [onSmartSearch, toast]);

  const handleConfirm = useCallback(async () => {
    if (selectedId == null) return;
    const index = parseInt(selectedId, 10);
    const selected = searchResults[index];
    if (!selected) return;

    setIsConfirming(true);
    try {
      if (onConfirm) {
        await onConfirm(selected.externalId, selected.title);
      } else {
        await confirmMatch.mutateAsync({
          seriesId,
          provider,
          externalSeriesId: selected.externalId,
          externalSeriesTitle: selected.title,
        });
      }
      onOpenChange(false);
    } catch (error) {
      console.error('Failed to confirm match:', error);
      toast({
        title: 'Failed to confirm mapping',
        description: error instanceof Error ? error.message : 'The mapping could not be saved. Please try again.',
        variant: 'destructive',
      });
    } finally {
      setIsConfirming(false);
    }
  }, [selectedId, searchResults, seriesId, provider, confirmMatch, onConfirm, onOpenChange, toast]);

  // Reset state when dialog opens and prefill search with series title
  const hasAutoSearched = useRef(false);
  React.useEffect(() => {
    if (open) {
      setSearchQuery(seriesTitle);
      setSearchResults([]);
      setSelectedId(null);
      setHasSearched(false);
      hasAutoSearched.current = false;
      setTimeout(() => {
        searchInputRef.current?.focus();
      }, 100);
    }
  }, [open, seriesTitle]);

  // Auto-search on mount with the pre-filled series title
  React.useEffect(() => {
    if (open && seriesTitle.trim() && !hasAutoSearched.current) {
      hasAutoSearched.current = true;
      // Small delay to let state settle before auto-searching
      const timer = setTimeout(async () => {
        setIsSearching(true);
        try {
          const result = await searchExternal.mutateAsync({ provider, query: seriesTitle });
          setSearchResults(result.results);
          setHasSearched(true);
        } finally {
          setIsSearching(false);
        }
      }, 200);
      return () => clearTimeout(timer);
    }
  }, [open, seriesTitle, provider, searchExternal]);

  // Compute cover URL with fallback to imageTemplateUrl
  const getCoverUrl = useCallback((result: ScrobblerSearchResult): string | undefined => {
    if (result.coverUrl) return result.coverUrl;
    if (imageTemplateUrl && result.externalId) {
      return imageTemplateUrl.replace('{0}', result.externalId);
    }
    return undefined;
  }, [imageTemplateUrl]);

  // Combobox suggestions for the search box: the primary series title, any explicit candidate
  // titles, and the alternative-titles string (callers join it with ' · ').
  const searchOptions = React.useMemo(() => {
    const opts = [
      seriesTitle,
      ...(candidateTitles ?? []),
      ...(seriesAltTitles ? seriesAltTitles.split('·').map((t) => t.trim()) : []),
    ]
      .map((t) => t?.trim())
      .filter((t): t is string => !!t);
    return Array.from(new Set(opts));
  }, [seriesTitle, candidateTitles, seriesAltTitles]);

  return (
    <TooltipProvider>
      <Dialog open={open} onOpenChange={onOpenChange}>
        <DialogContent className="w-[65vw] max-w-4xl h-[70vh] max-h-[70vh] flex flex-col">
          <DialogHeader>
            <DialogTitle>Search {ScrobblerProvider[provider]}</DialogTitle>
            <DialogDescription>
              Find a matching series on {ScrobblerProvider[provider]}
            </DialogDescription>
          </DialogHeader>

          {/* Original series info — Rensaio cover, title, alternative titles */}
          <div className="flex items-center gap-3 p-3 rounded-lg border bg-muted/30">
            {seriesThumbnail && (
              <Tooltip>
                <TooltipTrigger asChild>
                  <LazyImage
                    src={formatThumbnailUrl(seriesThumbnail)}
                    alt={seriesTitle}
                    className="h-20 w-14 rounded object-cover flex-shrink-0 cursor-pointer"
                  />
                </TooltipTrigger>
                <TooltipPrimitive.Portal>
                  <TooltipContent side="right" className="p-0 bg-transparent border-none shadow-none">
                    <div className="relative w-48 aspect-[3/4]">
                      <img
                        src={formatThumbnailUrl(seriesThumbnail)}
                        alt={seriesTitle}
                        className="w-full h-full object-cover rounded-md border border-secondary"
                      />
                    </div>
                  </TooltipContent>
                </TooltipPrimitive.Portal>
              </Tooltip>
            )}
            <div className="min-w-0">
              <p className="text-sm font-medium">{seriesTitle}</p>
              {seriesAltTitles && (
                <p className="text-xs text-muted-foreground mt-1">{seriesAltTitles}</p>
              )}
            </div>
          </div>

          {/* Search input + Smart Search. The Smart Search button appears only when the caller
              supplies a smart loader; it ignores the textbox and ranks by every title + covers. */}
          <div className="flex gap-2">
            <ComboInput
              ref={searchInputRef}
              wrapperClassName="flex-1"
              placeholder={`Search by title, #id or ${ScrobblerProvider[provider]} URL...`}
              value={searchQuery}
              onValueChange={setSearchQuery}
              options={searchOptions}
              onKeyDown={(e) => e.key === 'Enter' && handleSearch()}
            />
            <Button onClick={handleSearch} disabled={isSearching || isSmartSearching || !searchQuery.trim()}>
              <Search className="h-4 w-4 mr-1" />
              Search
            </Button>
            {onSmartSearch && (
              <Button
                variant="secondary"
                onClick={handleSmartSearch}
                disabled={isSearching || isSmartSearching}
                title="Search using every title of the series and its existing covers"
              >
                <Sparkles className={`h-4 w-4 mr-1 ${isSmartSearching ? 'animate-pulse' : ''}`} />
                {isSmartSearching ? 'Smart Searching…' : 'Smart Search'}
              </Button>
            )}
          </div>

          {/* Search results grid — with scrollbar-gutter for right gap */}
          {searchResults.length > 0 && (
            <div className="flex-1 overflow-y-auto" style={{ scrollbarGutter: 'stable' }}>
              <div className="grid grid-cols-3 sm:grid-cols-4 lg:grid-cols-5 gap-2 p-0.5">
                {searchResults.map((result, idx) => {
                  const coverUrl = getCoverUrl(result);
                  const altLines = result.alternateTitles?.length
                    ? result.alternateTitles.join('\n')
                    : '';
                  // Use the row index as identity: externalId may be empty/duplicated across
                  // providers (MangaBaka/MangaUpdates), which caused all rows to highlight together.
                  const rowKey = `${idx}`;
                  return (
                    <div
                      key={rowKey}
                      className={`cursor-pointer rounded-lg border transition-all duration-200 hover:shadow-md ${
                        selectedId === rowKey
                          ? 'ring-2 ring-primary shadow-md'
                          : 'hover:ring-1 hover:ring-gray-300'
                      }`}
                      onClick={() => setSelectedId(
                        selectedId === rowKey ? null : rowKey
                      )}
                    >
                      {coverUrl ? (
                        <Tooltip>
                          <TooltipTrigger asChild>
                            <div className="aspect-[3/4] relative bg-muted">
                              <LazyImage
                                src={coverUrl}
                                alt={result.title}
                                className="h-full w-full object-cover"
                              />
                            </div>
                          </TooltipTrigger>
                          <TooltipPrimitive.Portal>
                            <TooltipContent side="right" className="p-0 bg-transparent border-none shadow-none">
                              <div className="relative w-48 aspect-[3/4]">
                                <img
                                  src={coverUrl}
                                  alt={result.title}
                                  className="w-full h-full object-cover rounded-md border border-secondary"
                                />
                              </div>
                            </TooltipContent>
                          </TooltipPrimitive.Portal>
                        </Tooltip>
                      ) : (
                        <div className="aspect-[3/4] relative bg-muted flex items-center justify-center text-muted-foreground text-xs">
                          No cover
                        </div>
                      )}
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <div className={`p-1.5 text-center ${
                            selectedId === rowKey
                              ? 'bg-primary text-primary-foreground'
                              : 'bg-card'
                          }`}>
                            <p className="text-xs font-medium truncate">{result.title}</p>
                            <p className="text-[10px] text-muted-foreground mt-0.5 line-clamp-1">
                              {result.alternateTitles && result.alternateTitles.length > 0
                                ? result.alternateTitles.join(', ')
                                : '\u00A0'}
                            </p>
                            {result.type && (
                              <p className="text-[10px] text-muted-foreground mt-0.5">
                                {result.year ? `${result.type} (${result.year})` : result.type}
                              </p>
                            )}
                          </div>
                        </TooltipTrigger>
                        <TooltipPrimitive.Portal>
                          <TooltipContent align="start" collisionPadding={20} className="max-w-[200px]">
                            <p className="text-xs font-medium">{result.title}</p>
                            {altLines && (
                              <>
                                <hr className="my-1 border-t border-border" />
                                <div className="text-[11px] text-muted-foreground whitespace-pre-line">
                                  {altLines}
                                </div>
                              </>
                            )}
                            {result.type && (
                              <>
                                <hr className="my-1 border-t border-border" />
                                <p className="text-[11px] text-muted-foreground">{result.type}</p>
                              </>
                            )}
                          </TooltipContent>
                        </TooltipPrimitive.Portal>
                      </Tooltip>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Empty / loading states */}
          {(isSearching || isSmartSearching) && searchResults.length === 0 && (
            <p className="text-sm text-muted-foreground text-center py-4">
              {isSmartSearching ? 'Smart searching...' : 'Searching...'}
            </p>
          )}
          {hasSearched && !isSearching && !isSmartSearching && searchResults.length === 0 && searchQuery && (
            <p className="text-sm text-muted-foreground text-center py-4">
              No results found for "{searchQuery}"
            </p>
          )}

          <DialogFooter>
            <Button variant="outline" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button
              onClick={handleConfirm}
              disabled={!selectedId || confirmMatch.isPending || isConfirming}
            >
              <Check className="h-4 w-4 mr-1" />
              {confirmMatch.isPending || isConfirming ? 'Confirming...' : confirmLabel}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </TooltipProvider>
  );
}