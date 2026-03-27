import { haptic } from './helpers.js';

// Each row: { key, value, isSecret, fromProfile, isLocked }
// fromProfile=true: came from profile template, secret with empty value = "keep as-is"
// isLocked=true: value cannot be overridden; shown read-only, not sent to server
let overrideRows = [];

export function initEnvOverrides() {
  document.getElementById('add-env-override-btn').addEventListener('click', addRow);
}

export function resetEnvOverrides() {
  overrideRows = [];
  renderRows();
}

export function loadFromProfile(envVars) {
  overrideRows = envVars.map(v => ({
    key: v.key,
    value: v.isSecret ? '' : v.value,   // secrets come masked — show empty
    isSecret: v.isSecret,
    fromProfile: true,
    isLocked: v.isLocked || false,
  }));
  renderRows();
}

export function showEnvOverridesSection(visible) {
  document.getElementById('env-overrides-section').style.display = visible ? 'block' : 'none';
  if (!visible) resetEnvOverrides();
}

// Returns array of EnvVar overrides to send, or null on validation error.
// Profile-origin secrets left empty → not sent (keep server value).
export function getEnvOverrides() {
  syncFromDom();

  const toSend = overrideRows.filter(r => {
    if (!r.key.trim()) return false;
    if (r.isLocked) return false; // locked vars are never sent as overrides
    if (r.fromProfile && r.isSecret && r.value === '') return false; // unchanged
    return true;
  });

  const seen = new Set();
  for (const r of toSend) {
    const k = r.key.trim();
    if (seen.has(k)) {
      const errEl = document.getElementById('env-override-error');
      errEl.textContent = 'Duplicate key: ' + k;
      errEl.style.display = 'block';
      haptic('error');
      return null;
    }
    seen.add(k);
  }
  document.getElementById('env-override-error').style.display = 'none';

  return toSend.map(r => ({
    key: r.key.trim(),
    value: r.value,
    isSecret: r.isSecret,
  }));
}

function addRow() {
  overrideRows.push({ key: '', value: '', isSecret: false, fromProfile: false });
  renderRows();
  haptic('impact');
  const keys = document.querySelectorAll('#env-override-rows .envvar-key');
  if (keys.length) keys[keys.length - 1].focus();
}

function removeRow(idx) {
  overrideRows.splice(idx, 1);
  renderRows();
  haptic('impact');
}

function syncFromDom() {
  const rows = document.querySelectorAll('#env-override-rows .envvar-row');
  rows.forEach((row, i) => {
    if (!overrideRows[i] || overrideRows[i].isLocked) return;

    overrideRows[i].key = row.querySelector('.envvar-key').value;
    overrideRows[i].value = row.querySelector('.envvar-val').value;
    overrideRows[i].isSecret = row.querySelector('.envvar-secret').checked;
  });
}

function renderRows() {
  const container = document.getElementById('env-override-rows');
  container.textContent = '';

  for (let i = 0; i < overrideRows.length; i++) {
    const row = overrideRows[i];
    const rowDiv = document.createElement('div');
    rowDiv.className = 'envvar-row';

    const keyInput = document.createElement('input');
    keyInput.className = 'form-input envvar-key';
    keyInput.placeholder = 'KEY';
    keyInput.value = row.key;
    if (row.fromProfile) keyInput.readOnly = true;

    const valInput = document.createElement('input');
    valInput.className = 'form-input envvar-val';
    valInput.type = row.isSecret ? 'password' : 'text';
    valInput.placeholder = row.fromProfile && row.isSecret ? '(unchanged)' : 'value';
    valInput.value = row.value;

    if (row.isLocked) {
      valInput.disabled = true;
      valInput.placeholder = '(locked)';

      const lockedSpan = document.createElement('span');
      lockedSpan.className = 'secret-label';
      lockedSpan.textContent = 'Locked';

      rowDiv.appendChild(keyInput);
      rowDiv.appendChild(valInput);
      rowDiv.appendChild(lockedSpan);
      container.appendChild(rowDiv);
      continue;
    }

    const secretLabel = document.createElement('label');
    secretLabel.className = 'secret-label';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'envvar-secret';
    checkbox.checked = row.isSecret;
    checkbox.disabled = row.fromProfile;
    checkbox.addEventListener('change', () => {
      syncFromDom();
      overrideRows[i].isSecret = checkbox.checked;
      renderRows();
    });
    secretLabel.appendChild(checkbox);
    secretLabel.appendChild(document.createTextNode(' Secret'));

    const removeBtn = document.createElement('button');
    removeBtn.className = 'btn-icon';
    removeBtn.textContent = '\u00d7';
    removeBtn.setAttribute('aria-label', 'Remove');
    removeBtn.addEventListener('click', () => {
      syncFromDom();
      removeRow(i);
    });

    rowDiv.appendChild(keyInput);
    rowDiv.appendChild(valInput);
    rowDiv.appendChild(secretLabel);
    rowDiv.appendChild(removeBtn);
    container.appendChild(rowDiv);
  }
}
