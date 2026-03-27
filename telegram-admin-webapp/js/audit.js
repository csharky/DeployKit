import { api } from './api.js';
import { renderSkeletons } from './helpers.js';

export async function loadAuditLog() {
  const container = document.getElementById('audit-list');
  renderSkeletons(container, 'audit', 5);

  try {
    const entries = await api('GET', '/api/audit?count=100');
    renderAuditLog(entries);
  } catch (e) {
    container.innerHTML = `<div class="error-msg" style="display:block">Failed to load audit log</div>`;
  }
}

function renderAuditLog(entries) {
  const container = document.getElementById('audit-list');
  if (entries.length === 0) {
    container.innerHTML = '<div class="empty-state">No audit entries yet</div>';
    return;
  }

  container.innerHTML = '';
  for (const entry of entries) {
    const el = document.createElement('div');
    el.className = 'card';
    el.innerHTML = `
      <div class="card-header">
        <span class="card-profile">${escHtml(actionLabel(entry.action))}</span>
        <span class="badge badge-pending" style="font-size:10px">${escHtml(entry.keyName)}</span>
      </div>
      <div class="card-meta" style="font-size:11px;color:var(--tg-theme-hint-color,#999)">${formatDate(entry.timestamp)}</div>
      ${entry.details ? `<div class="card-meta" style="font-family:monospace;font-size:11px;word-break:break-all">${escHtml(entry.details)}</div>` : ''}
    `;
    container.appendChild(el);
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

const ACTION_LABELS = {
  'job.created': 'Job created',
  'job.cancelled': 'Job cancelled',
  'profile.created': 'Profile created',
  'profile.updated': 'Profile updated',
  'profile.deleted': 'Profile deleted',
  'apikey.created': 'API key created',
  'apikey.revoked': 'API key revoked',
};

function actionLabel(action) {
  return ACTION_LABELS[action] || action;
}

function formatDate(iso) {
  try {
    const d = new Date(iso);
    return d.toLocaleDateString() + ' ' + d.toLocaleTimeString();
  } catch { return iso; }
}

function escHtml(str) {
  return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
