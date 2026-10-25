import { apiClient } from '@/lib/api/client';
import {
  type ContributionMappingGroup,
  type ContributionMappingsPage,
  type ContributionMappingLinkRequest,
  type ScrobblerProvider,
  type SeriesMappingStatus,
  type SmartSearchResponse,
} from '@/lib/api/types';

const BASE = '/api/contribution-mappings';

export const contributionMappingsService = {
  async list(params: {
    filter?: 'all' | 'unmatched' | 'blocked' | 'manualignored';
    verified?: 'all' | 'verified' | 'unverified';
    page?: number;
    pageSize?: number;
    status?: SeriesMappingStatus | null;
    name?: string;
  }): Promise<ContributionMappingsPage> {
    const query = new URLSearchParams();
    query.set('filter', params.filter ?? 'unmatched');
    query.set('verified', params.verified ?? 'all');
    query.set('page', String(params.page ?? 0));
    query.set('pageSize', String(params.pageSize ?? 50));
    if (params.status != null) query.set('status', params.status.toString());
    if (params.name && params.name.trim().length > 0) query.set('name', params.name.trim());
    return apiClient.get<ContributionMappingsPage>(`${BASE}?${query.toString()}`);
  },

  async getMapping(mappingId: string): Promise<ContributionMappingGroup> {
    return apiClient.get<ContributionMappingGroup>(`${BASE}/mappings/${mappingId}`);
  },

  async scanMapping(mappingId: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/scan`);
  },

  /**
   * Smart Search: searches the provider with EVERY title of the mapping (no query text), scores
   * with the title + image algorithm using the mapping's existing covers, and returns the top hits.
   */
  async smartSearchMapping(mappingId: string, provider: ScrobblerProvider): Promise<SmartSearchResponse> {
    const params = new URLSearchParams({ provider: provider.toString() });
    return apiClient.post<SmartSearchResponse>(`${BASE}/mappings/${mappingId}/smart-search?${params.toString()}`);
  },

  async linkMapping(request: ContributionMappingLinkRequest): Promise<{ message: string }> {
    const params = new URLSearchParams({ provider: request.provider.toString() });
    return apiClient.post<{ message: string }>(
      `${BASE}/mappings/${request.mappingId}/link?${params.toString()}`,
      {
        externalSeriesId: request.externalSeriesId,
        externalSeriesTitle: request.externalSeriesTitle,
      },
    );
  },

  /** Block a specific provider identity (id). The key is required; the provider stays searchable for a different id. */
  async blockMapping(mappingId: string, provider: string, key: string): Promise<{ message: string }> {
    const params = new URLSearchParams({ key });
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/block?${params.toString()}`);
  },

  /** Clear a block on a specific provider identity (id) — returns it to a single Unmatched row. */
  async unblockMapping(mappingId: string, provider: string, key: string): Promise<{ message: string }> {
    const params = new URLSearchParams({ key });
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/unblock?${params.toString()}`);
  },

  async ignoreMapping(mappingId: string, provider: string, forever: boolean): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/ignore`, { forever });
  },

  async ignoreAllUnmatched(mappingId: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/ignore-all`);
  },

  /** Mark every live source entry of the mapping as Verified (replicated to the cloud). */
  async verifyMapping(mappingId: string): Promise<{ message: string; count: number }> {
    return apiClient.post<{ message: string; count: number }>(`${BASE}/mappings/${mappingId}/verify`);
  },

  /** Clear VerifiedDateUTC on every live source entry (LOCAL-only; the cloud value is sticky). */
  async unverifyMapping(mappingId: string): Promise<{ message: string; count: number }> {
    return apiClient.post<{ message: string; count: number }>(`${BASE}/mappings/${mappingId}/unverify`);
  },

  async unlinkMapping(mappingId: string, provider: string): Promise<{ message: string }> {
    return apiClient.delete<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}`);
  },

  async scanAll(): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/scan`);
  },

  /** Merge two or more contribution mappings into one series. */
  async joinMappings(mappingIds: string[]): Promise<{ message: string; targetId: string }> {
    return apiClient.post<{ message: string; targetId: string }>(`${BASE}/join`, { mappingIds });
  },

  /** Split a multi-source contribution into one series per source entry. */
  async splitMapping(mappingId: string): Promise<{ message: string; seriesIds: string[] }> {
    return apiClient.post<{ message: string; seriesIds: string[] }>(`${BASE}/mappings/${mappingId}/split`);
  },
};
