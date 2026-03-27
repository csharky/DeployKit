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

// ─── Skeleton loading ───

function skeletonLine(...classes) {
  const el = document.createElement('div');
  el.className = 'skeleton-line ' + classes.join(' ');
  return el;
}

const SKELETON_BUILDERS = {
  job(card) {
    card.appendChild(skeletonLine('skeleton-line-title'));
    card.appendChild(skeletonLine('skeleton-line-meta'));
    const footer = document.createElement('div');
    footer.className = 'skeleton-footer';
    footer.appendChild(skeletonLine('skeleton-line-badge'));
    card.appendChild(footer);
  },
  agent(card) {
    const header = document.createElement('div');
    header.className = 'skeleton-header';
    const left = document.createElement('span');
    left.style.display = 'flex';
    left.style.alignItems = 'center';
    left.style.gap = '6px';
    const dot = document.createElement('span');
    dot.className = 'skeleton-dot';
    left.appendChild(dot);
    left.appendChild(skeletonLine('skeleton-line-title'));
    left.querySelector('.skeleton-line-title').style.width = '100px';
    header.appendChild(left);
    header.appendChild(skeletonLine('skeleton-line-badge'));
    card.appendChild(header);
    card.appendChild(skeletonLine('skeleton-line-meta'));
    card.querySelector('.skeleton-line-meta').style.width = '50%';
  },
  profile(card) {
    card.appendChild(skeletonLine('skeleton-line-title'));
    card.appendChild(skeletonLine('skeleton-line-short'));
  },
  apikey(card) {
    const header = document.createElement('div');
    header.className = 'skeleton-header';
    header.appendChild(skeletonLine('skeleton-line-title'));
    header.querySelector('.skeleton-line-title').style.width = '40%';
    header.appendChild(skeletonLine('skeleton-line-badge'));
    card.appendChild(header);
    card.appendChild(skeletonLine('skeleton-line-meta'));
    card.querySelector('.skeleton-line-meta').style.width = '50%';
    card.appendChild(skeletonLine('skeleton-line-long'));
    card.querySelector('.skeleton-line-long').style.width = '65%';
    card.appendChild(skeletonLine('skeleton-line-short'));
    card.querySelector('.skeleton-line-short').style.width = '35%';
  },
  audit(card) {
    const header = document.createElement('div');
    header.className = 'skeleton-header';
    header.appendChild(skeletonLine('skeleton-line-title'));
    header.querySelector('.skeleton-line-title').style.width = '50%';
    header.appendChild(skeletonLine('skeleton-line-badge'));
    card.appendChild(header);
    card.appendChild(skeletonLine('skeleton-line-short'));
    card.querySelector('.skeleton-line-short').style.width = '35%';
    card.appendChild(skeletonLine('skeleton-line-long'));
    card.querySelector('.skeleton-line-long').style.width = '75%';
  },
};

export function renderSkeletons(container, type, count = 3) {
  container.textContent = '';
  const frag = document.createDocumentFragment();
  const build = SKELETON_BUILDERS[type];
  for (let i = 0; i < count; i++) {
    const card = document.createElement('div');
    card.className = 'skeleton-card';
    build(card);
    frag.appendChild(card);
  }
  container.appendChild(frag);
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
