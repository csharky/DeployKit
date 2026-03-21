import { state } from './state.js';
import { loadJobs } from './jobs.js';
import { loadAgents } from './agents.js';
import { loadProfiles, openCreateForm } from './profiles.js';
import { api } from './api.js';
import { haptic } from './helpers.js';

const SETTINGS_PAGES = new Set(['settings', 'connection', 'agents', 'profiles']);

// ─── Telegram BackButton ───
let _backHandler = null;
const _tgBack = window.Telegram?.WebApp?.BackButton ?? null;

if (_tgBack) {
  document.querySelectorAll('.btn-back').forEach(btn => btn.style.display = 'none');
}

function updateBackButton(backAction) {
  if (!_tgBack) return;
  if (_backHandler) {
    _tgBack.offClick(_backHandler);
    _backHandler = null;
  }
  if (backAction) {
    _backHandler = backAction;
    _tgBack.onClick(_backHandler);
    _tgBack.show();
  } else {
    _tgBack.hide();
  }
}

export function showPage(name) {
  document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
  document.getElementById(name + '-page')?.classList.add('active');

  const isSetup = name === 'setup';
  const settingsBtn = document.getElementById('settings-btn');
  settingsBtn.classList.toggle('hidden', isSetup);
  settingsBtn.classList.toggle('active', SETTINGS_PAGES.has(name));

  const fab = document.getElementById('fab-new');
  fab.classList.toggle('hidden', name !== 'jobs');
  document.getElementById('fab-gradient').classList.toggle('hidden', name !== 'jobs');
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
  } else if (name === 'new') {
    loadNewJobProfiles();
  } else if (name === 'settings') {
    updateSettingsHints();
  }

  updateBackButton((name === 'jobs' || name === 'setup') ? null : () => switchTab('jobs'));
}

export function openSettingsSection(name) {
  clearInterval(state.refreshTimer);
  state.activeStreams.forEach(ac => ac.abort());
  state.activeStreams.clear();
  state.durationTimers.forEach(id => clearInterval(id));
  state.durationTimers.clear();

  showPage(name);

  if (name === 'agents') {
    loadAgents();
  } else if (name === 'profiles') {
    loadProfiles();
  } else if (name === 'connection') {
    document.getElementById('settings-url').value = state.apiUrl;
    document.getElementById('settings-key').value = state.apiKey;
  }

  updateBackButton(() => backToSettings());
}

export function backToSettings() {
  clearInterval(state.refreshTimer);
  state.activeStreams.forEach(ac => ac.abort());
  state.activeStreams.clear();
  state.durationTimers.forEach(id => clearInterval(id));
  state.durationTimers.clear();

  showPage('settings');
  updateSettingsHints();
  updateBackButton(() => switchTab('jobs'));
}

function navigateToCreateProfile() {
  clearInterval(state.refreshTimer);
  state.activeStreams.forEach(ac => ac.abort());
  state.activeStreams.clear();
  state.durationTimers.forEach(id => clearInterval(id));
  state.durationTimers.clear();

  showPage('profiles');
  openCreateForm();
  updateBackButton(() => backToSettings());
}

function updateSettingsHints() {
  api('GET', '/api/agents')
    .then(agents => {
      const live = agents.filter(a => a.alive).length;
      const hint = document.getElementById('settings-agents-hint');
      if (hint) hint.textContent = live > 0 ? `${live} online` : `${agents.length} total`;
    })
    .catch(() => {});

  api('GET', '/api/profiles')
    .then(profiles => {
      const hint = document.getElementById('settings-profiles-hint');
      if (hint) hint.textContent = profiles.length > 0 ? String(profiles.length) : '';
    })
    .catch(() => {});
}

function loadNewJobProfiles() {
  const select = document.getElementById('job-profile-select');
  const btn = document.getElementById('submit-btn');
  const errDiv = document.getElementById('new-job-error');

  errDiv.style.display = 'none';

  const hint = document.getElementById('new-job-profile-hint');
  hint.textContent = '';
  select.textContent = '';
  const loadingOpt = document.createElement('option');
  loadingOpt.value = '';
  loadingOpt.textContent = 'Loading\u2026';
  select.appendChild(loadingOpt);
  select.disabled = true;
  btn.disabled = true;

  api('GET', '/api/profiles')
    .then(function(profiles) {
      select.textContent = '';
      hint.textContent = '';
      if (profiles.length === 0) {
        const emptyOpt = document.createElement('option');
        emptyOpt.value = '';
        emptyOpt.textContent = 'No profiles yet';
        select.appendChild(emptyOpt);
        select.disabled = true;
        btn.disabled = true;

        const emptyBtn = document.createElement('button');
        emptyBtn.className = 'btn';
        emptyBtn.style.marginTop = '8px';
        emptyBtn.textContent = '+ Create your first profile';
        emptyBtn.addEventListener('click', navigateToCreateProfile);
        hint.appendChild(emptyBtn);
      } else {
        const promptOpt = document.createElement('option');
        promptOpt.value = '';
        promptOpt.textContent = 'Select a profile\u2026';
        select.appendChild(promptOpt);
        for (const profile of profiles) {
          const opt = document.createElement('option');
          opt.value = profile.id;
          opt.textContent = profile.name;
          select.appendChild(opt);
        }
        select.disabled = false;
        btn.disabled = false;

        const link = document.createElement('span');
        link.className = 'create-profile-link';
        link.textContent = '+ Create new profile';
        link.addEventListener('click', navigateToCreateProfile);
        hint.appendChild(link);
      }
    })
    .catch(function() {
      select.textContent = '';
      hint.textContent = '';
      const errOpt = document.createElement('option');
      errOpt.value = '';
      errOpt.textContent = 'Error';
      select.appendChild(errOpt);
      select.disabled = true;
      btn.disabled = true;
      errDiv.textContent = 'Failed to load profiles \u2014 tap to retry';
      errDiv.style.display = 'block';
      errDiv.onclick = function() { loadNewJobProfiles(); };
      haptic('error');
    });
}
