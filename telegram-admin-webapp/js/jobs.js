import { state } from './state.js';
import { api, consumeSseStream } from './api.js';
import { esc, fmt, buildTime, relTime, fmtDuration, haptic, TERMINAL_STATUSES } from './helpers.js';
import { switchTab } from './navigation.js';

const REFRESH_INTERVAL = 15000;

// ─── Duration timers ───

export function startDurationTimer(jobId, startedAt) {
  if (state.durationTimers.has(jobId)) return;
  const start = new Date(startedAt).getTime();
  const tick = () => {
    const dur = fmtDuration(Date.now() - start);
    document.querySelector(`.card[data-job-id="${jobId}"] .build-time`)?.textContent !== undefined &&
      (document.querySelector(`.card[data-job-id="${jobId}"] .build-time`).textContent = dur);
    const detailSpan = document.getElementById('duration-' + jobId);
    if (detailSpan) detailSpan.textContent = dur;
  };
  tick();
  state.durationTimers.set(jobId, setInterval(tick, 1000));
}

export function stopDurationTimer(jobId) {
  if (state.durationTimers.has(jobId)) {
    clearInterval(state.durationTimers.get(jobId));
    state.durationTimers.delete(jobId);
  }
}

// ─── Card creation ───

function createJobCard(j) {
  const tpl = document.getElementById('tpl-job-card');
  const card = tpl.content.cloneNode(true).firstElementChild;
  card.dataset.jobId = j.jobId;
  const profileName = j.profileSnapshot?.name || j.profileId;
  card.querySelector('.card-profile').textContent = profileName;
  card.querySelector('.card-details').id = 'detail-' + j.jobId;
  updateJobCardMeta(card, j);
  card.addEventListener('click', () => toggleCard(card, j.jobId));

  if (j.status === 'pending') {
    const btn = document.createElement('button');
    btn.className = 'btn btn-cancel';
    btn.textContent = 'Cancel';
    btn.addEventListener('click', e => { e.stopPropagation(); cancelJob(j.jobId); });
    card.appendChild(btn);
  }

  const canRunJobs = state.permissions === null || state.permissions.includes('jobs:run');
  if (TERMINAL_STATUSES.includes(j.status) && canRunJobs) {
    const restartBtn = document.createElement('button');
    restartBtn.className = 'btn-restart-icon';
    restartBtn.setAttribute('aria-label', 'Restart');
    restartBtn.innerHTML = '<svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor"><path d="M17.65 6.35C16.2 4.9 14.21 4 12 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08c-.82 2.33-3.04 4-5.65 4-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/></svg>';
    restartBtn.addEventListener('click', e => { e.stopPropagation(); restartJob(j); });
    card.querySelector('.card-footer').appendChild(restartBtn);
  }

  return card;
}

function updateJobCardMeta(card, j) {
  const badge = card.querySelector('.badge');
  badge.className = `badge badge-${j.status}`;
  badge.textContent = j.status;

  const shortId = j.jobId.slice(0, 7);
  const time = j.startedAt ? relTime(j.startedAt) : relTime(j.createdAt);
  const dur = buildTime(j);
  const meta = card.querySelector('.card-meta');
  meta.innerHTML = '';
  meta.append(
    document.createTextNode(shortId + ' \u00b7 ' + time),
    ...(dur ? [document.createTextNode(' \u00b7 '), Object.assign(document.createElement('span'), { className: 'build-time', textContent: dur })] : [])
  );
}

// ─── Load jobs (with DOM recycling) ───

export async function loadJobs() {
  const el = document.getElementById('jobs-list');
  try {
    const jobs = await api('GET', '/api/jobs');
    if (!jobs || jobs.length === 0) {
      state.activeStreams.forEach((ac, key) => { if (!key.startsWith('agent:')) ac.abort(); });
      el.innerHTML = '<div class="empty">No jobs yet</div>';
      return;
    }

    const jobIds = new Set(jobs.map(j => j.jobId));

    // Remove cards for jobs no longer in list
    el.querySelectorAll('.card[data-job-id]').forEach(card => {
      const id = card.dataset.jobId;
      if (!jobIds.has(id)) {
        if (state.activeStreams.has(id)) { state.activeStreams.get(id).abort(); state.activeStreams.delete(id); }
        stopDurationTimer(id);
        card.remove();
      }
    });

    el.querySelectorAll('.empty, .error-msg').forEach(node => node.remove());

    let prevCard = null;
    for (const j of jobs) {
      let card = el.querySelector(`.card[data-job-id="${j.jobId}"]`);
      if (card) {
        if (card.classList.contains('expanded')) {
          updateJobCardMeta(card, j);
        } else {
          const newCard = createJobCard(j);
          card.replaceWith(newCard);
          card = newCard;
        }
      } else {
        card = createJobCard(j);
        if (prevCard?.nextSibling) el.insertBefore(card, prevCard.nextSibling);
        else if (prevCard) el.appendChild(card);
        else el.prepend(card);
      }

      if (j.status === 'running' && j.startedAt) startDurationTimer(j.jobId, j.startedAt);
      else stopDurationTimer(j.jobId);

      prevCard = card;
    }
  } catch (e) {
    el.innerHTML = `<div class="error-msg">${e.message}</div>`;
  }
}

// ─── Toggle job card ───

export async function toggleCard(el, jobId) {
  if (el.classList.contains('expanded')) {
    el.classList.remove('expanded');
    stopDurationTimer(jobId);
    if (state.activeStreams.has(jobId)) {
      state.activeStreams.get(jobId).abort();
      state.activeStreams.delete(jobId);
    }
    return;
  }

  el.classList.add('expanded');
  const detailEl = document.getElementById('detail-' + jobId);
  detailEl.innerHTML = '<div class="loading"><span class="spinner"></span></div>';

  let j;
  try {
    j = await api('GET', `/api/jobs/${jobId}`);
  } catch (e) {
    detailEl.innerHTML = `<div class="error-msg">${e.message}</div>`;
    return;
  }

  const lines = [];
  lines.push(`<div class="card-meta">Created: ${fmt(j.createdAt)}</div>`);
  if (j.startedAt) lines.push(`<div class="card-meta">Started: ${fmt(j.startedAt)}</div>`);
  if (j.completedAt) lines.push(`<div class="card-meta">Completed: ${fmt(j.completedAt)}</div>`);
  if (j.startedAt) {
    lines.push(`<div class="card-meta">Duration: <span id="duration-${jobId}">${buildTime(j)}</span></div>`);
  }
  if (j.error) lines.push(`<div class="error-msg" style="text-align:left;padding:8px 0">${esc(j.error)}</div>`);
  if (j.artifactPath) lines.push(`<div class="card-meta">Artifact: ${esc(j.artifactPath)}</div>`);
  lines.push(`<div class="logs" id="logs-${jobId}">${esc(j.logs || '')}</div>`);
  detailEl.innerHTML = lines.join('');

  if (TERMINAL_STATUSES.includes(j.status)) return;

  if (j.startedAt) startDurationTimer(jobId, j.startedAt);

  const ac = new AbortController();
  state.activeStreams.set(jobId, ac);
  const logsEl = document.getElementById('logs-' + jobId);

  (async () => {
    try {
      await consumeSseStream(
        `${state.apiUrl}/api/jobs/${jobId}/stream`,
        event => {
          if (event.type === 'log_delta') {
            logsEl.textContent += event.content;
            logsEl.scrollTop = logsEl.scrollHeight;
          } else if (event.type === 'status') {
            const badge = el.querySelector('.badge');
            if (badge) { badge.className = `badge badge-${event.status}`; badge.textContent = event.status; }
            if (TERMINAL_STATUSES.includes(event.status)) stopDurationTimer(jobId);
          }
        },
        ac.signal
      );
    } catch (e) {
      if (e.name !== 'AbortError') logsEl.textContent += '\n[stream error: ' + e.message + ']';
    } finally {
      state.activeStreams.delete(jobId);
    }
  })();
}

// ─── Restart job ───

export function restartJob(job) {
  state.prefillJob = job;
  haptic('impact');
  switchTab('new');
}

// ─── Cancel job ───

export async function cancelJob(jobId) {
  try {
    await api('DELETE', `/api/jobs/${jobId}`);
    haptic('impact');
    loadJobs();
  } catch (e) {
    alert('Cancel failed: ' + e.message);
  }
}
