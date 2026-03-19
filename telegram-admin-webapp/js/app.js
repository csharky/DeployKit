import { state } from './state.js';
import { storageGet, storageSet } from './storage.js';
import { switchTab, showPage } from './navigation.js';
import { api } from './api.js';
import { haptic } from './helpers.js';

// ─── Init ───

(async function init() {
  if (window.Telegram?.WebApp) {
    Telegram.WebApp.ready();
    Telegram.WebApp.expand();
    Telegram.WebApp.requestFullscreen?.();
  }

  const stored = await storageGet(['deploy_url', 'deploy_key']);
  state.apiUrl = stored.deploy_url || '';
  state.apiKey = stored.deploy_key || '';

  if (!state.apiUrl || !state.apiKey) {
    showPage('setup');
  } else {
    switchTab('jobs');
  }

  registerListeners();
})();

// ─── Event listeners ───

function registerListeners() {
  document.getElementById('tab-jobs').addEventListener('click', () => switchTab('jobs'));
  document.getElementById('tab-agents').addEventListener('click', () => switchTab('agents'));
  document.getElementById('tab-new').addEventListener('click', () => switchTab('new'));
  document.getElementById('settings-btn').addEventListener('click', () => switchTab('settings'));

  document.getElementById('setup-connect-btn').addEventListener('click', saveConfig);
  document.getElementById('settings-save-btn').addEventListener('click', saveSettings);
  document.getElementById('submit-btn').addEventListener('click', createJob);
}

// ─── Config ───

function saveConfig() {
  state.apiUrl = document.getElementById('cfg-url').value.replace(/\/+$/, '');
  state.apiKey = document.getElementById('cfg-key').value;
  if (!state.apiUrl || !state.apiKey) return;
  storageSet('deploy_url', state.apiUrl);
  storageSet('deploy_key', state.apiKey);
  switchTab('jobs');
}

function saveSettings() {
  state.apiUrl = document.getElementById('settings-url').value.replace(/\/+$/, '');
  state.apiKey = document.getElementById('settings-key').value;
  if (!state.apiUrl || !state.apiKey) return;
  storageSet('deploy_url', state.apiUrl);
  storageSet('deploy_key', state.apiKey);
  haptic('success');
  switchTab('jobs');
}

// ─── Create job ───

async function createJob() {
  const profile = document.getElementById('job-profile').value.trim();
  const platform = document.getElementById('job-platform').value;
  if (!profile) { document.getElementById('job-profile').focus(); return; }

  const btn = document.getElementById('submit-btn');
  btn.disabled = true;
  btn.textContent = 'Creating...';
  try {
    await api('POST', '/api/jobs', { profile, platform });
    haptic('success');
    document.getElementById('job-profile').value = '';
    switchTab('jobs');
  } catch (e) {
    alert('Failed: ' + e.message);
    haptic('error');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Create Job';
  }
}
