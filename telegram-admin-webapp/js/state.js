export const state = {
  apiUrl: '',
  apiKey: '',
  permissions: null, // string[] | null — populated after /api/me; null means not yet fetched
  refreshTimer: null,
  activeStreams: new Map(), // jobId or 'agent:{agentId}' → AbortController
  durationTimers: new Map(), // jobId → intervalId
  prefillJob: null, // JobResponse to pre-fill the New Job form with
};
