import { state } from './state.js';
import { storageGet, storageSet } from './storage.js';
import { switchTab, showPage, openSettingsSection, backToSettings } from './navigation.js';
import { api } from './api.js';
import { haptic } from './helpers.js';
import { getEnvOverrides, initEnvOverrides } from './env-overrides.js';
import { initApiKeyForm } from './apikeys.js';

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
    fetchAndApplyPermissions();
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
  document.getElementById('settings-apikeys-row').addEventListener('click', () => openSettingsSection('apikeys'));
  document.getElementById('settings-audit-row').addEventListener('click', () => openSettingsSection('audit'));

  document.getElementById('connection-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('agents-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('profiles-settings-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('apikeys-back-btn').addEventListener('click', () => backToSettings());
  document.getElementById('audit-back-btn').addEventListener('click', () => backToSettings());

  document.getElementById('setup-connect-btn').addEventListener('click', saveConfig);
  document.getElementById('settings-save-btn').addEventListener('click', saveSettings);
  document.getElementById('submit-btn').addEventListener('click', createJob);

  initEnvOverrides();
  initApiKeyForm();
}

// ─── Config ───

function saveConfig() {
  state.apiUrl = document.getElementById('cfg-url').value.replace(/\/+$/, '');
  state.apiKey = document.getElementById('cfg-key').value;
  if (!state.apiUrl || !state.apiKey) return;
  storageSet('deploy_url', state.apiUrl);
  storageSet('deploy_key', state.apiKey);
  switchTab('jobs');
  fetchAndApplyPermissions();
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

// ─── Permissions ───

export async function fetchAndApplyPermissions() {
  try {
    const me = await api('GET', '/api/me');
    state.permissions = me.permissions ?? [];
  } catch (_) {
    state.permissions = [];
  }
  applyPermissions();
}

function applyPermissions() {
  const p = state.permissions ?? [];
  const canRunJobs = p.includes('jobs:run');
  const canManageKeys = p.includes('apikeys:manage');

  // FAB — hide if no jobs:run permission (only relevant when on jobs page)
  const fab = document.getElementById('fab-new');
  if (!canRunJobs) {
    fab.classList.add('hidden');
    document.getElementById('fab-gradient').classList.add('hidden');
  }

  // Settings rows — hide sections the key cannot access
  document.getElementById('settings-apikeys-row').style.display = canManageKeys ? '' : 'none';
  document.getElementById('settings-audit-row').style.display = canManageKeys ? '' : 'none';
}

// ─── Create job ───

async function createJob() {
  const profileId = document.getElementById('job-profile-select').value;
  if (!profileId) { document.getElementById('job-profile-select').focus(); return; }

  const envOverrides = getEnvOverrides();
  if (envOverrides === null) return; // validation failed

  const btn = document.getElementById('submit-btn');
  btn.disabled = true;
  btn.textContent = 'Creating...';
  try {
    const body = { profileId };
    if (envOverrides.length > 0) body.envOverrides = envOverrides;
    await api('POST', '/api/jobs', body);
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
