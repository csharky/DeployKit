import { state } from './state.js';
import { api, consumeSseStream } from './api.js';
import { esc, relTime, renderSkeletons } from './helpers.js';

const AGENT_LOG_LINES = 100;

// ─── Card creation ───

function createAgentCard(a) {
  const tpl = document.getElementById('tpl-agent-card');
  const card = tpl.content.cloneNode(true).firstElementChild;
  card.dataset.agentId = a.agentId;
  updateAgentCardMeta(card, a);
  card.addEventListener('click', () => toggleAgentLogs(card, a.agentId));
  return card;
}

function updateAgentCardMeta(card, a) {
  const dot = card.querySelector('.agent-dot');
  dot.className = `agent-dot ${a.alive ? 'alive' : 'offline'}`;

  card.querySelector('.agent-name').textContent = a.agentId;

  const badge = card.querySelector('.badge');
  badge.className = `badge ${a.alive ? 'badge-completed' : 'badge-failed'}`;
  badge.textContent = a.alive ? 'online' : 'offline';

  card.querySelector('.card-meta').textContent = a.lastSeen ? 'Last seen: ' + relTime(a.lastSeen) : 'Never seen';
}

// ─── Load agents (with DOM recycling) ───

export async function loadAgents() {
  const el = document.getElementById('agents-list');
  if (!el.querySelector('.card[data-agent-id]')) {
    renderSkeletons(el, 'agent', 3);
  }
  try {
    const agents = await api('GET', '/api/agents');
    if (!agents || agents.length === 0) {
      el.innerHTML = '<div class="empty">No agents registered</div>';
      return;
    }

    const agentIds = new Set(agents.map(a => a.agentId));

    // Remove cards for agents no longer in list
    el.querySelectorAll('.card[data-agent-id]').forEach(card => {
      const id = card.dataset.agentId;
      if (!agentIds.has(id)) {
        const streamKey = 'agent:' + id;
        if (state.activeStreams.has(streamKey)) { state.activeStreams.get(streamKey).abort(); state.activeStreams.delete(streamKey); }
        card.remove();
      }
    });

    el.querySelectorAll('.empty, .error-msg, .skeleton-card').forEach(node => node.remove());

    let prevCard = null;
    for (const a of agents) {
      let card = el.querySelector(`.card[data-agent-id="${a.agentId}"]`);
      if (card) {
        // Always update meta (status/last-seen), preserve expanded logs
        updateAgentCardMeta(card, a);
      } else {
        card = createAgentCard(a);
        if (prevCard?.nextSibling) el.insertBefore(card, prevCard.nextSibling);
        else if (prevCard) el.appendChild(card);
        else el.prepend(card);
      }
      prevCard = card;
    }
  } catch (e) {
    el.innerHTML = `<div class="error-msg">${e.message}</div>`;
  }
}

// ─── Toggle agent logs ───

export async function toggleAgentLogs(el, agentId) {
  const streamKey = 'agent:' + agentId;
  if (el.classList.contains('expanded')) {
    el.classList.remove('expanded');
    if (state.activeStreams.has(streamKey)) {
      state.activeStreams.get(streamKey).abort();
      state.activeStreams.delete(streamKey);
    }
    return;
  }

  el.classList.add('expanded');
  const detail = el.querySelector('.card-details');

  let initialLines = [];
  try {
    const res = await api('GET', `/api/agent/${agentId}/logs?lines=${AGENT_LOG_LINES}`);
    initialLines = (res && res.lines) || [];
  } catch (e) {
    detail.innerHTML = `<div class="error-msg">${e.message}</div>`;
    return;
  }

  detail.innerHTML = `<div class="logs" id="agent-logs-${esc(agentId)}">${esc(initialLines.join('\n'))}</div>`;
  const logsEl = document.getElementById('agent-logs-' + agentId);

  const ac = new AbortController();
  state.activeStreams.set(streamKey, ac);

  (async () => {
    try {
      await consumeSseStream(
        `${state.apiUrl}/api/agent/${agentId}/logs/stream?from=${initialLines.length}`,
        event => {
          if (event.type === 'lines' && event.lines.length > 0) {
            logsEl.textContent += (logsEl.textContent ? '\n' : '') + event.lines.join('\n');
            logsEl.scrollTop = logsEl.scrollHeight;
          }
        },
        ac.signal
      );
    } catch (e) {
      if (e.name !== 'AbortError') logsEl.textContent += '\n[stream error: ' + e.message + ']';
    } finally {
      state.activeStreams.delete(streamKey);
    }
  })();
}
