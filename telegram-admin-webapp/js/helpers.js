export const TERMINAL_STATUSES = ['completed', 'failed', 'cancelled'];

export function esc(s) {
  if (!s) return '';
  const d = document.createElement('div');
  d.textContent = s;
  return d.innerHTML;
}

export function fmt(iso) {
  if (!iso) return '';
  return new Date(iso).toLocaleString();
}

export function fmtDuration(ms) {
  const s = Math.floor(ms / 1000);
  if (s < 0) return '0s';
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${m}m ${sec}s`;
  if (m > 0) return `${m}m ${sec}s`;
  return `${sec}s`;
}

export function buildTime(j) {
  if (!j.startedAt) return '';
  if (j.completedAt) return fmtDuration(new Date(j.completedAt) - new Date(j.startedAt));
  return fmtDuration(Date.now() - new Date(j.startedAt).getTime());
}

export function relTime(iso) {
  if (!iso) return '';
  const diff = Date.now() - new Date(iso).getTime();
  const s = Math.floor(diff / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return Math.floor(s / 60) + 'm ago';
  if (s < 86400) return Math.floor(s / 3600) + 'h ago';
  return Math.floor(s / 86400) + 'd ago';
}

export function haptic(type) {
  try {
    if (window.Telegram && Telegram.WebApp.HapticFeedback) {
      if (type === 'success') Telegram.WebApp.HapticFeedback.notificationOccurred('success');
      else if (type === 'error') Telegram.WebApp.HapticFeedback.notificationOccurred('error');
      else Telegram.WebApp.HapticFeedback.impactOccurred('light');
    }
  } catch (_) {}
}
