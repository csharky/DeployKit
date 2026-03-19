import { state } from './state.js';
import { loadJobs } from './jobs.js';
import { loadAgents } from './agents.js';

export function showPage(name) {
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));

  document.getElementById(name + '-page')?.classList.add('active');
  document.getElementById('tab-' + name)?.classList.add('active');

  const isSetup = name === 'setup';
  document.querySelector('.tabs').style.display = isSetup ? 'none' : 'flex';
  document.body.style.paddingBottom = isSetup ? '0' : 'calc(60px + env(safe-area-inset-bottom))';

  const settingsBtn = document.getElementById('settings-btn');
  settingsBtn.classList.toggle('hidden', isSetup);
  settingsBtn.classList.toggle('active', name === 'settings');
}

export function switchTab(name) {
  clearInterval(state.refreshTimer);
  state.activeStreams.forEach(ac => ac.abort());
  state.activeStreams.clear();
  state.durationTimers.forEach(id => clearInterval(id));
  state.durationTimers.clear();

  showPage(name);

  if (name === 'jobs') {
    loadJobs();
    state.refreshTimer = setInterval(loadJobs, 15000);
  } else if (name === 'agents') {
    loadAgents();
  } else if (name === 'settings') {
    document.getElementById('settings-url').value = state.apiUrl;
    document.getElementById('settings-key').value = state.apiKey;
  }
}
