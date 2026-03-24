import { haptic } from './helpers.js';

let overrideRows = [];
let expanded = false;

export function initEnvOverrides() {
  document.getElementById('env-overrides-toggle').addEventListener('click', toggleOverrides);
  document.getElementById('add-env-override-btn').addEventListener('click', addRow);
}

export function resetEnvOverrides() {
  overrideRows = [];
  expanded = false;
  document.getElementById('env-overrides-body').style.display = 'none';
  document.getElementById('env-overrides-chevron').textContent = '\u25B6';
  renderRows();
}

export function showEnvOverridesSection(visible) {
  document.getElementById('env-overrides-section').style.display = visible ? 'block' : 'none';
  if (!visible) resetEnvOverrides();
}

export function getEnvOverrides() {
  syncFromDom();
  const filled = overrideRows.filter(r => r.key.trim() !== '');

  // validate duplicates
  const seen = new Set();
  for (const r of filled) {
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

  return filled.map(r => ({
    key: r.key.trim(),
    value: r.value,
    isSecret: r.isSecret,
  }));
}

function toggleOverrides() {
  expanded = !expanded;
  document.getElementById('env-overrides-body').style.display = expanded ? 'block' : 'none';
  document.getElementById('env-overrides-chevron').textContent = expanded ? '\u25BC' : '\u25B6';
}

function addRow() {
  overrideRows.push({ key: '', value: '', isSecret: false });
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
  const container = document.getElementById('env-override-rows');
  const rows = container.querySelectorAll('.envvar-row');
  overrideRows = Array.from(rows).map(row => ({
    key: row.querySelector('.envvar-key').value.trim(),
    value: row.querySelector('.envvar-val').value,
    isSecret: row.querySelector('.envvar-secret').checked,
  }));
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

    const valInput = document.createElement('input');
    valInput.className = 'form-input envvar-val';
    valInput.type = row.isSecret ? 'password' : 'text';
    valInput.placeholder = 'value';
    valInput.value = row.value;

    const secretLabel = document.createElement('label');
    secretLabel.className = 'secret-label';
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'envvar-secret';
    checkbox.checked = row.isSecret;
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
