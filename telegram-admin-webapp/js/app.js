import { state } from './state.js';
import { storageGet, storageSet } from './storage.js';
import { switchTab, showPage, openSettingsSection, backToSettings } from './navigation.js';
import { api } from './api.js';
import { haptic } from './helpers.js';

// ─── Init ───

(async function init() {
  if (window.Telegram?.WebApp) {
    Telegram.WebApp.ready();
    Telegram.WebApp.expand();
    const p = Telegram.WebApp.platform;
    const isMobile = p === 'android' || p === 'ios' || p === 'android_x';
    if (isMobile) Telegram.WebApp.requestFullscreen?.();
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
  document.getElementById('fab-new').addEventListener('click', () => switchTab('new'));
  document.getElementById('new-job-back-btn').addEventListener('click', () => switchTab('jobs'));
  document.getElementById('settings-btn').addEventListener('click', () => switchTab('settings'));

  document.getElementById('settings-connection-row').addEventListener('click', () => openSettingsSection('connection'));
  document.getElementById('settings-agents-row').addEventListener('click', () => openSettingsSection('agents'));
  document.getElementById('settings-profiles-row').addEventListener('click', () => openSettingsSection('profiles'));

  document.getElementById('connection-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('agents-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('profiles-settings-back-btn').addEventListener('click', () => backToSettings());

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
  backToSettings();
}

// ─── Create job ───

async function createJob() {
  const profileId = document.getElementById('job-profile-select').value;
  if (!profileId) { document.getElementById('job-profile-select').focus(); return; }

  const btn = document.getElementById('submit-btn');
  btn.disabled = true;
  btn.textContent = 'Creating...';
  try {
    await api('POST', '/api/jobs', { profileId });
    haptic('success');
    switchTab('jobs');
  } catch (e) {
    alert('Failed: ' + e.message);
    haptic('error');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Create Job';
  }
}
