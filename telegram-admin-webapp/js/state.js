export const state = {
  apiUrl: '',
  apiKey: '',
  refreshTimer: null,
  activeStreams: new Map(), // jobId or 'agent:{agentId}' → AbortController
  durationTimers: new Map(), // jobId → intervalId
};
