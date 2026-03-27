import { api } from './api.js';
import { haptic, renderSkeletons } from './helpers.js';

export async function loadApiKeys() {
  const container = document.getElementById('apikeys-list-items');
  renderSkeletons(container, 'apikey', 3);

  try {
    const keys = await api('GET', '/api/apikeys');
    renderApiKeys(keys);
  } catch (e) {
    container.innerHTML = `<div class="error-msg" style="display:block">Failed to load API keys</div>`;
  }
}

function renderApiKeys(keys) {
  const container = document.getElementById('apikeys-list-items');
  if (keys.length === 0) {
    container.innerHTML = '<div class="empty-state">No API keys yet</div>';
    return;
  }

  container.innerHTML = '';
  for (const key of keys) {
    const card = document.createElement('div');
    card.className = 'card' + (key.revoked ? ' card-disabled' : '');
    card.innerHTML = `
      <div class="card-header">
        <span class="card-profile">${escHtml(key.name)}</span>
        <span class="badge ${key.revoked ? 'badge-failed' : 'badge-completed'}">${key.revoked ? 'Revoked' : 'Active'}</span>
      </div>
      <div class="card-meta" style="font-family:monospace;font-size:12px">${escHtml(key.keyPrefix)}••••••••</div>
      <div class="card-meta" style="font-size:11px;margin-top:2px">${formatPermissions(key.permissions)}</div>
      <div class="card-meta" style="font-size:11px;color:var(--tg-theme-hint-color,#999)">Created ${formatDate(key.createdAt)}</div>
      ${!key.revoked ? `<div style="margin-top:8px"><button class="btn btn-cancel btn-sm" data-id="${escHtml(key.id)}" data-name="${escHtml(key.name)}">Revoke</button></div>` : ''}
    `;
    card.querySelectorAll('[data-id]').forEach(btn => {
      btn.addEventListener('click', () => revokeApiKey(btn.dataset.id, btn.dataset.name));
    });
    container.appendChild(card);
  }
}

async function revokeApiKey(id, name) {
  if (!confirm(`Revoke API key "${name}"? This cannot be undone.`)) return;
  try {
    await api('DELETE', '/api/apikeys/' + id);
    haptic('success');
    await loadApiKeys();
  } catch (e) {
    alert('Failed to revoke: ' + e.message);
    haptic('error');
  }
}

export function initApiKeyForm() {
  const addBtn = document.getElementById('add-apikey-btn');
  const form = document.getElementById('apikey-form');
  const reveal = document.getElementById('apikey-reveal');
  const cancelBtn = document.getElementById('apikey-cancel-btn');
  const createBtn = document.getElementById('apikey-create-btn');
  const copyBtn = document.getElementById('apikey-copy-btn');
  const doneBtn = document.getElementById('apikey-reveal-done-btn');
  const errDiv = document.getElementById('apikey-error');

  addBtn.addEventListener('click', () => {
    form.style.display = 'block';
    reveal.style.display = 'none';
    addBtn.style.display = 'none';
    document.getElementById('apikey-name').value = '';
    document.querySelectorAll('#apikey-permissions input').forEach(cb => cb.checked = false);
    errDiv.style.display = 'none';
  });

  cancelBtn.addEventListener('click', () => {
    form.style.display = 'none';
    addBtn.style.display = 'block';
  });

  createBtn.addEventListener('click', async () => {
    const name = document.getElementById('apikey-name').value.trim();
    const perms = [...document.querySelectorAll('#apikey-permissions input:checked')].map(cb => cb.value);

    if (!name) { showError('Name is required'); return; }
    if (perms.length === 0) { showError('Select at least one permission'); return; }

    createBtn.disabled = true;
    createBtn.classList.add('btn-loading');
    errDiv.style.display = 'none';

    try {
      const created = await api('POST', '/api/apikeys', { name, permissions: perms });
      form.style.display = 'none';
      reveal.style.display = 'block';
      document.getElementById('apikey-reveal-value').value = created.key;
      haptic('success');
      await loadApiKeys();
    } catch (e) {
      showError(e.message || 'Failed to create key');
      haptic('error');
    } finally {
      createBtn.disabled = false;
      createBtn.classList.remove('btn-loading');
    }
  });

  copyBtn.addEventListener('click', () => {
    const val = document.getElementById('apikey-reveal-value').value;
    navigator.clipboard?.writeText(val).then(() => haptic('success'));
  });

  doneBtn.addEventListener('click', () => {
    reveal.style.display = 'none';
    addBtn.style.display = 'block';
  });

  function showError(msg) {
    errDiv.textContent = msg;
    errDiv.style.display = 'block';
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

const PERM_LABELS = {
  'jobs:run': 'Run jobs',
  'jobs:read': 'Read jobs',
  'profiles:read': 'Read profiles',
  'profiles:write': 'Edit profiles',
  'apikeys:manage': 'Manage keys',
};

function formatPermissions(perms) {
  return (perms || []).map(p => PERM_LABELS[p] || p).join(' · ');
}

function formatDate(iso) {
  try { return new Date(iso).toLocaleDateString(); } catch { return iso; }
}

function escHtml(str) {
  return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
