import { state } from './state.js';

export async function api(method, path, body) {
  const opts = {
    method,
    headers: { 'X-API-Key': state.apiKey, 'Content-Type': 'application/json' },
  };
  if (body) opts.body = JSON.stringify(body);
  const res = await fetch(state.apiUrl + path, opts);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

export async function consumeSseStream(url, onEvent, signal) {
  const res = await fetch(url, { headers: { 'X-API-Key': state.apiKey }, signal });
  if (!res.ok) throw new Error(`Stream ${res.status}`);
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let boundary;
    while ((boundary = buffer.indexOf('\n\n')) !== -1) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      for (const line of block.split('\n')) {
        if (line.startsWith('data: ')) {
          try { onEvent(JSON.parse(line.slice(6))); } catch (_) {}
        }
      }
    }
  }
}
