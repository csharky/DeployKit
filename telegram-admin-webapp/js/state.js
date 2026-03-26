export const state = {
  apiUrl: '',
  apiKey: '',
  refreshTimer: null,
  activeStreams: new Map(), // jobId or 'agent:{agentId}' → AbortController
  durationTimers: new Map(), // jobId → intervalId
  prefillJob: null, // JobResponse to pre-fill the New Job form with
};
