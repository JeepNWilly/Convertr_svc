// Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU GPL v3 or later; no warranty.
// Queue tab, confirmation dialog, undo list. Everything is built with textContent (no HTML from the server is ever injected).
import { get, post, del } from './api.js';

const $ = (id) => document.getElementById(id);
const fmtBytes = (b) => {
  if (!b) return '0';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; let v = b;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i >= 3 ? v.toFixed(1) : Math.round(v)) + ' ' + u[i];
};
const fmtDur = (s) => {
  if (s == null || !isFinite(s)) return '';
  s = Math.round(s); const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return h > 0 ? `${h} h ${m} min` : m > 0 ? `${m} min ${s % 60} s` : `${s} s`;
};
const STATE_LABEL = {
  Pending: 'waiting', CopyingIn: 'copying to scratch', Encoding: 'encoding', Verifying: 'verifying', CopyingBack: 'copying back',
  Swapping: 'swapping in', Done: 'done', NotWorthIt: 'kept original', Failed: 'failed', Skipped: 'skipped',
};

let jobs = new Map();       // id -> view
let status = null;
let progress = {};
let onAfterStart = () => {};

export function init({ afterStart }) {
  onAfterStart = afterStart || (() => {});
  $('q-pause').addEventListener('click', () => act('/api/queue/pause'));
  $('q-resume').addEventListener('click', () => act('/api/queue/resume'));
  $('q-stop-after').addEventListener('click', () => act('/api/queue/stop-after-current'));
  $('q-stop').addEventListener('click', () => { if (confirm('Stop now? The running file is cancelled (its temporary files are deleted and the original is untouched). Waiting files stay in the queue.')) act('/api/queue/stop'); });
  $('q-resume-queue').addEventListener('click', () => act('/api/convert/resume-queue'));
  $('q-discard').addEventListener('click', () => { if (confirm('Discard the queue? Nothing that was already converted is changed.')) act('/api/queue', 'DELETE'); });
  $('undo-selected').addEventListener('click', undoSelected);
  $('confirm-go').addEventListener('click', startConversion);
  $('q-log-box').addEventListener('toggle', () => { if ($('q-log-box').open) loadLog(); });
}

// ---------- log (the newest run log on the server; new lines arrive by the "log" event) ----------
const LOG_MAX = 1000;
let logLines = [];
function renderLog() {
  const el = $('q-log');
  const atEnd = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
  el.textContent = logLines.join('\n');
  if (atEnd) el.scrollTop = el.scrollHeight;
}
export async function loadLog() {
  const r = await get('/api/queue/log?lines=500').catch(() => null);
  if (!r) return;
  logLines = r.lines;
  $('q-log-file').textContent = r.path || 'none yet';
  renderLog();
  $('q-log').scrollTop = $('q-log').scrollHeight;
}
function addLogLine(line) {
  logLines.push(line);
  if (logLines.length > LOG_MAX) logLines.splice(0, logLines.length - LOG_MAX);
  if ($('q-log-box').open) renderLog();
}

async function act(url, method) {
  $('q-error').textContent = '';
  try { status = method === 'DELETE' ? await del(url) : await post(url); renderStatus(status); if (method === 'DELETE') { jobs = new Map(); renderJobs(); } await loadJobs(); }
  catch (err) { $('q-error').textContent = err.message; }
}

export function onEvent(type, data) {
  if (type === 'queue') { status = data; renderStatus(data); if (data.state !== 'running') { loadJobs(); if ($('q-log-box').open) loadLog(); } }
  else if (type === 'job') { jobs.set(data.id, data); renderJobs(); }
  else if (type === 'progress') { progress = data; renderProgress(); }
  else if (type === 'undo') loadUndo();
  else if (type === 'log') addLogLine(data.line);
}

export async function load() {
  status = await get('/api/queue'); renderStatus(status);
  await Promise.all([loadJobs(), loadUndo(), loadLog()]);
  progress = await get('/api/queue/progress').catch(() => ({})); renderProgress();
}

async function loadJobs() {
  const r = await get('/api/queue/jobs?take=500').catch(() => null);
  if (!r) return;
  jobs = new Map(r.jobs.map((j) => [j.id, j]));
  renderJobs();
}

function renderStatus(s) {
  const active = s.state === 'running' || s.state === 'paused' || s.state === 'stopping';
  $('q-pause').hidden = s.state !== 'running'; $('q-resume').hidden = s.state !== 'paused';
  $('q-stop-after').hidden = !(s.state === 'running' || s.state === 'paused'); $('q-stop').hidden = !active;
  $('q-resume-queue').hidden = !(s.hasQueue && !active && s.pending > 0);
  $('q-discard').hidden = !(s.hasQueue && !active);
  const badge = $('queue-badge');
  const left = s.pending + s.active;
  badge.hidden = !(active && left > 0); badge.textContent = String(left);
  const title = !s.hasQueue ? 'No conversion yet'
    : (s.dryRun ? 'Dry run ' : 'Conversion ') + ({ running: 'running', paused: 'paused', stopping: 'stopping...', finished: 'finished', idle: 'waiting' }[s.state] || s.state);
  $('queue-title').textContent = title;
  const parts = [];
  if (s.hasQueue) {
    parts.push(`${s.done} done`, `${s.notWorthIt} kept original`, `${s.failed} failed`, `${s.skipped} skipped`, `${s.pending + s.active} to go`);
    if (s.savedBytes > 0) parts.push(`saved ${fmtBytes(s.savedBytes)}`);
    parts.push(`${s.mode} / ${s.storage}`);
  }
  $('q-text').textContent = (parts.join(' - ') + (s.message ? '. ' + s.message : '')).trim();
  $('q-progress').hidden = !active;
}

function renderProgress() {
  const p = progress || {};
  if (p.totalBytes) {
    $('q-progress').value = Math.round(100 * (p.processedBytes || 0) / p.totalBytes);
    const eta = p.overallEtaSec != null ? ` - about ${fmtDur(p.overallEtaSec)} left` : '';
    $('q-progress').title = `${fmtBytes(p.processedBytes)} of ${fmtBytes(p.totalBytes)}${eta}`;
  }
  if (p.jobId != null && jobs.has(p.jobId)) {
    const j = jobs.get(p.jobId); j._frac = p.jobFraction; j._speed = p.speed; j._eta = p.etaSec; renderJobs();
  }
}

function renderJobs() {
  const body = $('q-body'); body.replaceChildren();
  const rank = (j) => ['Done', 'NotWorthIt', 'Failed', 'Skipped'].includes(j.state) ? 2 : j.state === 'Pending' ? 1 : 0;
  const list = [...jobs.values()].sort((a, b) => rank(a) - rank(b) || a.id - b.id);
  for (const j of list.slice(0, 500)) {
    const tr = document.createElement('tr');
    const td = (text, cls) => { const c = document.createElement('td'); c.textContent = text; if (cls) c.className = cls; tr.append(c); return c; };
    td(j.path, 'name').title = j.path;
    td(STATE_LABEL[j.state] || j.state, 'st-' + j.state);
    td(fmtBytes(j.sizeBytes), 'num');
    td(j.state === 'Done' ? `${fmtBytes(j.outputBytes)} (-${j.savingPercent}%)` : '', 'num');
    const prog = document.createElement('td');
    if (j.state === 'CopyingIn' || j.state === 'CopyingBack') {
      const pr = document.createElement('progress'); pr.max = 100; pr.value = Math.round(100 * (j.copyFraction || 0)); prog.append(pr, ' ' + (j.copyMBps ? Math.round(j.copyMBps) + ' MB/s' : ''));
    } else if (j.state === 'Encoding') {
      const pr = document.createElement('progress'); pr.max = 100; pr.value = Math.round(100 * (j._frac || 0)); prog.append(pr, ` ${j._speed ? j._speed.toFixed(1) + 'x' : ''}${j._eta != null ? ' - ' + fmtDur(j._eta) : ''}`);
    } else prog.textContent = j.message || '';
    tr.append(prog);
    const act = document.createElement('td');
    if (j.state === 'Pending' && status && status.state !== 'running') {
      const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Remove'; b.addEventListener('click', async () => { try { await del('/api/queue/jobs/' + j.id); await loadJobs(); } catch (e) { $('q-error').textContent = e.message; } });
      act.append(b);
    }
    tr.append(act);
    body.append(tr);
  }
}

// ---------- confirmation ----------
export async function confirmAndStart() {
  const c = await get('/api/convert/preview');
  const dl = $('confirm-info'); dl.replaceChildren();
  const row = (k, v) => { const dt = document.createElement('dt'); dt.textContent = k; const dd = document.createElement('dd'); dd.textContent = v; dl.append(dt, dd); };
  row('Files', String(c.files)); row('Source size', fmtBytes(c.sourceBytes));
  row('Expected result', `about ${fmtBytes(c.estOutBytes)} (saving about ${fmtBytes(c.estSaveBytes)})`);
  row('Encoder', `${c.mode} - ${c.backend}, ${c.workers} at a time`);
  row('Storage', c.storage === 'scratch' ? `scratch disk ${c.scratchFolder}` : 'in place on the library');
  if (c.storage === 'scratch') row('Scratch needed', `${fmtBytes(c.scratchNeedBytes)} at once (free ${fmtBytes(c.scratchFreeBytes)}, budget ${fmtBytes(c.scratchBudgetBytes)})`);
  row('Originals', c.originals === 'MoveToHolding' ? 'moved to _Convertr_Originals (undoable)' : c.originals);
  const fill = (id, list) => { const ul = $(id); ul.replaceChildren(); for (const t of list) { const li = document.createElement('li'); li.textContent = t; ul.append(li); } };
  fill('confirm-warn', c.warnings); fill('confirm-block', c.blockers);
  $('confirm-go').disabled = !c.canStart;
  $('confirm-dry').checked = false;
  $('confirm-dialog').showModal();
}

async function startConversion() {
  $('confirm-go').disabled = true;
  try {
    await post('/api/convert/start', { dryRun: $('confirm-dry').checked });
    $('confirm-dialog').close();
    await load(); onAfterStart();
  } catch (err) {
    const li = document.createElement('li'); li.textContent = err.message; $('confirm-block').replaceChildren(li);
  } finally { $('confirm-go').disabled = false; }
}

// ---------- undo ----------
let undoItems = [];
async function loadUndo() {
  const r = await get('/api/undo').catch(() => null); if (!r) return;
  undoItems = r.items;
  const body = $('undo-body'); body.replaceChildren();
  for (const i of undoItems) {
    const tr = document.createElement('tr');
    const c0 = document.createElement('td'); const cb = document.createElement('input'); cb.type = 'checkbox'; cb.disabled = !i.canUndo; cb.dataset.path = i.path; cb.addEventListener('change', updUndo); c0.append(cb); tr.append(c0);
    const td = (t, cls) => { const c = document.createElement('td'); c.textContent = t; if (cls) c.className = cls; tr.append(c); };
    td(i.name, 'name'); td(fmtBytes(i.origSize), 'num'); td(fmtBytes(i.newSize), 'num'); td(i.status);
    body.append(tr);
  }
  if (undoItems.length === 0) { const tr = document.createElement('tr'); const c = document.createElement('td'); c.colSpan = 5; c.className = 'muted'; c.textContent = 'Nothing to undo yet.'; tr.append(c); body.append(tr); }
  updUndo();
}
function updUndo() { $('undo-selected').disabled = !$('undo-body').querySelector('input:checked'); }
async function undoSelected() {
  const paths = [...$('undo-body').querySelectorAll('input:checked')].map((c) => c.dataset.path);
  if (!paths.length || !confirm(`Undo ${paths.length} file(s)? The originals come back; the converted files are kept in _Convertr_Undone.`)) return;
  try {
    const r = await post('/api/undo', { paths });
    $('undo-msg').textContent = r.results.map((x) => `${x.success ? 'OK' : 'Not done'}: ${x.message}`).join(' | ');
  } catch (err) { $('undo-msg').textContent = err.message; }
  await loadUndo();
}
