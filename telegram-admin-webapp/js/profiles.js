import { api } from './api.js';
import { haptic, renderSkeletons } from './helpers.js';
import { state } from './state.js';

let envVarRows = [];  // array of { key, value, isSecret, isLocked }
let editingProfileId = null;  // null = create mode, string = edit mode

// ─── Load profiles list ───

export function loadProfiles() {
  const listEl = document.getElementById('profiles-list');
  const formEl = document.getElementById('profiles-form');
  listEl.style.display = 'block';
  formEl.style.display = 'none';

  const container = document.getElementById('profiles-list-items');
  renderSkeletons(container, 'profile', 3);

  api('GET', '/api/profiles')
    .then(profiles => {
      renderProfileList(profiles);
    })
    .catch(() => {
      const errDiv = document.createElement('div');
      errDiv.className = 'error-msg';
      errDiv.textContent = 'Failed to load profiles. Tap to retry.';
      errDiv.addEventListener('click', () => loadProfiles());
      container.textContent = '';
      container.appendChild(errDiv);
      haptic('error');
    });

  const canWrite = state.permissions === null || state.permissions.includes('profiles:write');
  const addBtn = document.getElementById('add-profile-btn');
  addBtn.style.display = canWrite ? '' : 'none';
  if (canWrite) addBtn.onclick = () => openCreateForm();
}

// ─── Render profile list ───

function renderProfileList(profiles) {
  const container = document.getElementById('profiles-list-items');
  container.textContent = '';

  if (profiles.length === 0) {
    const emptyDiv = document.createElement('div');
    emptyDiv.className = 'empty';

    const titleDiv = document.createElement('div');
    titleDiv.style.fontWeight = '600';
    titleDiv.style.marginBottom = '8px';
    titleDiv.textContent = 'No profiles yet';

    const subtitleDiv = document.createElement('div');
    subtitleDiv.textContent = 'Tap Add Profile to create your first build profile.';

    emptyDiv.appendChild(titleDiv);
    emptyDiv.appendChild(subtitleDiv);
    container.appendChild(emptyDiv);
    return;
  }

  for (const profile of profiles) {
    const card = document.createElement('div');
    card.className = 'card';
    card.dataset.profileId = profile.id;

    const header = document.createElement('div');
    header.className = 'card-header';

    const nameSpan = document.createElement('span');
    nameSpan.className = 'card-profile';
    nameSpan.textContent = profile.name;

    const meta = document.createElement('div');
    meta.className = 'card-meta';
    meta.textContent = profile.steps.length + ' step' + (profile.steps.length !== 1 ? 's' : '');

    header.appendChild(nameSpan);
    card.appendChild(header);
    card.appendChild(meta);

    card.addEventListener('click', () => openEditForm(profile.id));
    container.appendChild(card);
  }
}

// ─── Open create form ───

export function openCreateForm() {
  editingProfileId = null;

  document.getElementById('profile-form-title').textContent = 'New Profile';
  document.getElementById('profile-save-btn').textContent = 'Create Profile';
  document.getElementById('profile-delete-btn').style.display = 'none';
  document.getElementById('profile-name').value = '';
  document.getElementById('profile-workdir').value = '';

  envVarRows = [];
  renderEnvVarRows();

  document.getElementById('steps-textarea').value = '- ';

  document.getElementById('envvar-error').style.display = 'none';
  document.getElementById('profile-error').style.display = 'none';

  document.getElementById('profiles-list').style.display = 'none';
  document.getElementById('profiles-form').style.display = 'block';

  document.getElementById('profile-back-btn').onclick = () => loadProfiles();
  document.getElementById('add-envvar-btn').onclick = () => addEnvVarRow();
  document.getElementById('profile-save-btn').onclick = () => saveProfile();

  const canWriteProfiles = state.permissions === null || state.permissions.includes('profiles:write');
  document.getElementById('profile-save-btn').style.display = canWriteProfiles ? '' : 'none';
  document.getElementById('add-envvar-btn').style.display = canWriteProfiles ? '' : 'none';
}

// ─── Open edit form ───

async function openEditForm(profileId) {
  // Show form in loading state
  editingProfileId = profileId;
  document.getElementById('profiles-list').style.display = 'none';
  document.getElementById('profiles-form').style.display = 'block';
  document.getElementById('profile-form-title').textContent = 'Edit Profile';
  document.getElementById('profile-save-btn').textContent = 'Save Profile';
  document.getElementById('profile-delete-btn').style.display = 'block';
  document.getElementById('envvar-error').style.display = 'none';
  document.getElementById('profile-error').style.display = 'none';

  try {
    const profile = await api('GET', '/api/profiles/' + profileId);

    document.getElementById('profile-name').value = profile.name;
    document.getElementById('profile-workdir').value = profile.workingDirectory || '';

    // Populate env vars — secret values arrive as "***"
    // Store "***" in backing array; render empty password inputs with "(unchanged)" placeholder
    envVarRows = (profile.envVars || []).map(v => ({
      key: v.key,
      value: v.isSecret ? '***' : v.value,
      isSecret: v.isSecret,
      isLocked: v.isLocked || false,
    }));
    renderEnvVarRows();

    // Populate steps
    const steps = profile.steps && profile.steps.length > 0 ? profile.steps : [];
    document.getElementById('steps-textarea').value = steps.map(s => '- ' + s).join('\n');

    // Wire event listeners (use onclick= to avoid duplicates)
    document.getElementById('profile-back-btn').onclick = () => loadProfiles();
    document.getElementById('add-envvar-btn').onclick = () => addEnvVarRow();
    document.getElementById('profile-save-btn').onclick = () => saveProfile();
    document.getElementById('profile-delete-btn').onclick = () => confirmDeleteProfile();

    const canWrite = state.permissions === null || state.permissions.includes('profiles:write');
    document.getElementById('profile-save-btn').style.display = canWrite ? '' : 'none';
    document.getElementById('profile-delete-btn').style.display = canWrite ? 'block' : 'none';
    document.getElementById('add-envvar-btn').style.display = canWrite ? '' : 'none';
  } catch (e) {
    const errEl = document.getElementById('profile-error');
    errEl.textContent = 'Failed to load profile: ' + e.message;
    errEl.style.display = 'block';
    haptic('error');
  }
}

// ─── Env var rows ───

function renderEnvVarRows() {
  const container = document.getElementById('envvar-rows');
  container.textContent = '';

  for (let i = 0; i < envVarRows.length; i++) {
    const row = envVarRows[i];
    const rowDiv = document.createElement('div');
    rowDiv.className = 'envvar-row';

    const keyInput = document.createElement('input');
    keyInput.className = 'form-input envvar-key';
    keyInput.placeholder = 'KEY';
    keyInput.value = row.key;
    keyInput.addEventListener('input', e => {
      envVarRows[i].key = e.target.value;
    });

    const valInput = document.createElement('input');
    valInput.className = 'form-input envvar-val';
    valInput.type = row.isSecret ? 'password' : 'text';
    valInput.placeholder = row.isSecret ? '(unchanged)' : 'value';
    if (!row.isSecret) {
      valInput.value = row.value;
    } else {
      valInput.value = row.value === '***' ? '' : row.value;
    }
    valInput.addEventListener('input', e => {
      envVarRows[i].value = e.target.value;
    });

    const secretLabel = document.createElement('label');
    secretLabel.className = 'secret-label';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'envvar-secret';
    checkbox.checked = row.isSecret;
    checkbox.addEventListener('change', e => {
      envVarRows[i].isSecret = e.target.checked;
      renderEnvVarRows();
    });
    secretLabel.appendChild(checkbox);
    secretLabel.appendChild(document.createTextNode(' Secret'));

    const lockLabel = document.createElement('label');
    lockLabel.className = 'secret-label';
    const lockCheckbox = document.createElement('input');
    lockCheckbox.type = 'checkbox';
    lockCheckbox.className = 'envvar-locked';
    lockCheckbox.checked = row.isLocked;
    lockCheckbox.addEventListener('change', e => {
      envVarRows[i].isLocked = e.target.checked;
    });
    lockLabel.appendChild(lockCheckbox);
    lockLabel.appendChild(document.createTextNode(' Lock'));

    const removeBtn = document.createElement('button');
    removeBtn.className = 'btn-icon';
    removeBtn.textContent = '\u00d7';
    removeBtn.setAttribute('aria-label', 'Remove');
    removeBtn.addEventListener('click', () => {
      haptic('impact');
      removeEnvVarRow(i);
    });

    rowDiv.appendChild(keyInput);
    rowDiv.appendChild(valInput);
    rowDiv.appendChild(secretLabel);
    rowDiv.appendChild(lockLabel);
    rowDiv.appendChild(removeBtn);
    container.appendChild(rowDiv);
  }
}

function addEnvVarRow() {
  envVarRows.push({ key: '', value: '', isSecret: false, isLocked: false });
  renderEnvVarRows();
  haptic('impact');
  const keys = document.querySelectorAll('.envvar-key');
  if (keys.length) keys[keys.length - 1].focus();
}

function removeEnvVarRow(idx) {
  envVarRows.splice(idx, 1);
  renderEnvVarRows();
}

// ─── Validation ───

function validateEnvVars() {
  const keys = envVarRows.map(r => r.key.trim()).filter(Boolean);
  const seen = new Set();
  for (const k of keys) {
    if (seen.has(k)) {
      const errEl = document.getElementById('envvar-error');
      errEl.textContent = 'Duplicate key: ' + k;
      errEl.style.display = 'block';
      haptic('error');
      return false;
    }
    seen.add(k);
  }
  document.getElementById('envvar-error').style.display = 'none';
  return true;
}

// ─── Collect form data ───

function collectFormData() {
  // Sync env var values from DOM (in case input events were missed)
  const keyInputs = document.querySelectorAll('.envvar-key');
  const valInputs = document.querySelectorAll('.envvar-val');
  const secretInputs = document.querySelectorAll('.envvar-secret');
  const lockedInputs = document.querySelectorAll('.envvar-locked');
  envVarRows = Array.from(keyInputs).map((keyEl, i) => ({
    key: keyEl.value.trim(),
    value: valInputs[i].value,
    isSecret: secretInputs[i].checked,
    isLocked: lockedInputs[i]?.checked ?? false,
  }));

  // Parse steps from textarea
  const stepsText = document.getElementById('steps-textarea').value;
  const steps = stepsText.split('\n')
    .map(l => l.trim())
    .filter(l => l.startsWith('-'))
    .map(l => l.replace(/^-\s*/, '').trim())
    .filter(Boolean);

  return {
    name: document.getElementById('profile-name').value.trim(),
    workingDirectory: document.getElementById('profile-workdir').value.trim(),
    envVars: envVarRows.filter(r => r.key !== '').map(r => ({
      key: r.key,
      value: r.isSecret && r.value === '' && editingProfileId ? '***' : r.value,
      isSecret: r.isSecret,
      isLocked: r.isLocked,
    })),
    steps,
  };
}

// ─── Save profile ───

async function saveProfile() {
  const data = collectFormData();

  // Client-side validation
  if (!data.name) {
    document.getElementById('profile-name').focus();
    haptic('error');
    return;
  }
  if (data.steps.length === 0) {
    const errEl = document.getElementById('profile-error');
    errEl.textContent = 'At least one step is required.';
    errEl.style.display = 'block';
    haptic('error');
    return;
  }
  if (!validateEnvVars()) return;

  const btn = document.getElementById('profile-save-btn');
  btn.disabled = true;
  document.getElementById('profile-error').style.display = 'none';

  try {
    if (editingProfileId) {
      await api('PUT', '/api/profiles/' + editingProfileId, data);
    } else {
      await api('POST', '/api/profiles', data);
    }
    haptic('success');
    loadProfiles();
  } catch (e) {
    const errEl = document.getElementById('profile-error');
    errEl.textContent = 'Failed to save: ' + e.message;
    errEl.style.display = 'block';
    haptic('error');
  } finally {
    btn.disabled = false;
  }
}

// ─── Delete profile ───

async function apiDeleteProfile(profileId) {
  const opts = {
    method: 'DELETE',
    headers: { 'X-API-Key': state.apiKey, 'Content-Type': 'application/json' },
  };
  const res = await fetch(state.apiUrl + '/api/profiles/' + profileId, opts);
  if (res.ok) return null;
  const text = await res.text();
  let msg = res.status + ' ' + res.statusText;
  try {
    const body = JSON.parse(text);
    if (body.error) msg = body.error;
  } catch (_) {}
  throw new Error(msg);
}

async function confirmDeleteProfile() {
  if (!editingProfileId) return;

  const doDelete = await new Promise(resolve => {
    if (window.Telegram?.WebApp?.showConfirm) {
      Telegram.WebApp.showConfirm('Delete this profile?', resolve);
    } else {
      resolve(confirm('Delete this profile?'));
    }
  });

  if (!doDelete) return;

  try {
    await apiDeleteProfile(editingProfileId);
    haptic('success');
    loadProfiles();
  } catch (e) {
    const errEl = document.getElementById('profile-error');
    errEl.textContent = e.message === 'Profile has active jobs'
      ? 'Cannot delete: profile has active jobs.'
      : 'Delete failed: ' + e.message;
    errEl.style.display = 'block';
    haptic('error');
  }
}

