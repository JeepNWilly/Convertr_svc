// Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU GPL v3 or later; no warranty.
import { get, post, put, del, setCsrf } from './api.js';
import { Grid } from './grid.js';
import * as queue from './queue.js';
import * as nas from './nas.js';

const $ = (id) => document.getElementById(id);
const fmtBytes = (b) => {
  if (!b) return '0';
  const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0; let v = b;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (i >= 3 ? v.toFixed(1) : Math.round(v)) + ' ' + u[i];
};
const fmtInt = (n) => n.toLocaleString();

// ---------- theme ----------
function applyTheme(mode) {
  const m = (mode || 'System').toLowerCase();
  document.documentElement.dataset.theme = m;
  try { localStorage.setItem('convertr.theme', mode); } catch { /* private mode */ }
  $('theme-select').value = mode || 'System';
}
try { applyTheme(localStorage.getItem('convertr.theme') || 'System'); } catch { applyTheme('System'); }

// ---------- start ----------
let settings = null;
let grid = null;
let events = null;
let filter = 'all';

async function start() {
  let state;
  try { state = await get('/api/auth/state'); } catch { show('auth'); $('login-error').textContent = 'The server is not reachable.'; $('login-form').hidden = false; return; }
  if (state.needsSetup) { show('auth'); $('setup-form').hidden = false; $('login-form').hidden = true; return; }
  if (!state.authenticated) { show('auth'); $('login-form').hidden = false; $('setup-form').hidden = true; $('login-password').focus(); return; }
  setCsrf(state.csrf);
  await enterApp();
}

function show(which) {
  $('auth').hidden = which !== 'auth';
  $('app').hidden = which !== 'app';
}

// ---------- login / setup ----------
$('login-form').addEventListener('submit', async (e) => {
  e.preventDefault(); $('login-error').textContent = '';
  try {
    const r = await post('/api/auth/login', { password: $('login-password').value });
    $('login-password').value = ''; setCsrf(r.csrf); await enterApp();
  } catch (err) { $('login-error').textContent = err.message; }
});

$('setup-form').addEventListener('submit', async (e) => {
  e.preventDefault(); $('setup-error').textContent = '';
  const pw = $('setup-password').value;
  if (pw !== $('setup-password2').value) { $('setup-error').textContent = 'The two passwords are not the same.'; return; }
  try {
    await post('/api/auth/setup', { token: $('setup-token').value.trim(), password: pw });
    const r = await post('/api/auth/login', { password: pw });
    ['setup-token', 'setup-password', 'setup-password2'].forEach((i) => { $(i).value = ''; });
    setCsrf(r.csrf); await enterApp();
  } catch (err) { $('setup-error').textContent = err.message; }
});

$('logout').addEventListener('click', async () => {
  try { await post('/api/auth/logout'); } catch { /* already out */ }
  if (events) { events.close(); events = null; }
  setCsrf(null); await start();
});

// ---------- app ----------
async function enterApp() {
  show('app');
  settings = await get('/api/settings');
  applyTheme(settings.values.Theme);
  $('media-folder').value = settings.values.MediaFolder || '';
  buildSettingsForm(); loadServerInfo();
  if (!grid) {
    grid = new Grid({ head: $('grid-head'), body: $('grid-body'), space: $('grid-space'), onToggle: toggleRow, onSort: () => {} });
  }
  queue.init({ afterStart: () => selectTab('queue') });
  nas.init({ mediaFolder: () => settings.values.MediaFolder || '' });
  connectEvents();
  queue.load();
  loadHardware();
  const st = await get('/api/scan');
  renderScan(st);
  if (st.rows > 0) showResults();
}

function selectTab(view) {
  document.querySelectorAll('.tabs button').forEach((x) => x.setAttribute('aria-selected', String(x.dataset.view === view)));
  $('view-convert').hidden = view !== 'convert';
  $('view-queue').hidden = view !== 'queue';
  $('view-setup').hidden = view !== 'setup';
  if (view === 'convert' && grid) grid.refresh();
  if (view === 'queue') queue.load();
  if (view === 'setup') nas.load();
}
document.querySelectorAll('.tabs button').forEach((b) => b.addEventListener('click', () => selectTab(b.dataset.view)));
$('convert-selected').addEventListener('click', () => queue.confirmAndStart().catch((err) => { $('selection-text').textContent = err.message; }));

$('theme-select').addEventListener('change', async () => {
  const mode = $('theme-select').value; applyTheme(mode);
  try { await put('/api/settings', { Theme: mode }); } catch { /* the theme still applies locally */ }
});

// ---------- scan ----------
$('scan').addEventListener('click', async () => {
  $('folder-error').textContent = '';
  const folder = $('media-folder').value.trim();
  try {
    await put('/api/settings', { MediaFolder: folder });
    renderScan(await post('/api/scan', { folder }));
  } catch (err) { $('folder-error').textContent = err.message; }
});
$('cancel-scan').addEventListener('click', () => del('/api/scan').catch(() => {}));
$('media-folder').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('scan').click(); });

let scanSeq = 0;
function renderScan(s) {
  // A fast scan can finish before the POST answer (a 'running' snapshot) arrives; never let an older snapshot undo a newer one.
  if (s.seq != null) { if (s.seq < scanSeq) return; scanSeq = s.seq; }
  const running = s.state === 'running';
  $('scan').disabled = running; $('browse').disabled = running; $('cancel-scan').hidden = !running;
  $('progress-box').hidden = !running;
  if (running) {
    const p = $('progress');
    if (s.total > 0) { p.max = s.total; p.value = s.done; } else { p.removeAttribute('value'); }
    $('progress-text').textContent = s.phase === 'Enumerating' ? 'Looking for video files...'
      : `Probing ${fmtInt(s.done)} of ${fmtInt(s.total)} (${fmtInt(s.fromCache)} from cache${s.failed ? ', ' + s.failed + ' failed' : ''})${s.current ? ' - ' + s.current : ''}`;
  }
  const note = $('scan-note');
  if (s.state === 'done') note.textContent = `Scan finished in ${s.elapsedSec.toFixed(1)} s: ${fmtInt(s.rows)} video files.` + (s.message ? ' ' + s.message : '');
  else if (s.state === 'cancelled') note.textContent = 'Scan cancelled.';
  else if (s.state === 'error') note.textContent = s.message || 'The scan failed.';
  else if (!running) note.textContent = '';
}

function showResults() { $('results').hidden = false; refreshAll(); }

async function refreshAll() {
  const sum = await get('/api/plan/summary');
  renderSummary(sum);
  if (grid) grid.reset(filter);
}

function renderSummary(s) {
  const box = $('summary'); box.replaceChildren();
  const card = (v, l) => { const d = document.createElement('div'); d.className = 'c'; const b = document.createElement('b'); b.textContent = v; const sp = document.createElement('span'); sp.textContent = l; d.append(b, sp); box.append(d); };
  card(fmtInt(s.total), 'video files');
  card(fmtBytes(s.totalBytes), 'total size');
  card(fmtInt(s.reencodeSize + s.reencodeCompat + s.remux + s.fixAudio), 'to convert');
  card(fmtInt(s.skip), 'skip (efficient)');
  card('~' + fmtBytes(s.estSaveMidBytes), 'estimated saving');
  const en = $('estimate-note'); if (en) en.textContent = s.estimateNote || '';
  if (s.error + s.review + s.warnings > 0) card(fmtInt(s.error + s.review + s.warnings), 'need attention');
  $('selection-text').textContent = s.selected === 0 ? 'Nothing selected.'
    : `${fmtInt(s.selected)} selected, ${fmtBytes(s.selectedBytes)}, about ${fmtBytes(s.selectedEstSaveBytes)} saved`;
  $('select-none').disabled = s.selected === 0;
  $('convert-selected').disabled = s.selected === 0;
}

// ---------- filters, search, selection ----------
$('filters').addEventListener('click', (e) => {
  const b = e.target.closest('button'); if (!b) return;
  filter = b.dataset.filter;
  $('filters').querySelectorAll('button').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
  grid.reset(filter, $('search').value);
});
let searchTimer = 0;
$('search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(() => grid.reset(filter, $('search').value), 250); });

async function toggleRow(id, selected) {
  await put('/api/plan/selection', { ids: [id], selected });
  renderSummary(await get('/api/plan/summary'));
}
$('select-convert').addEventListener('click', async () => {
  await put('/api/plan/selection', { selected: true, allMatching: 'convert', q: $('search').value || null });
  renderSummary(await get('/api/plan/summary')); grid.refresh();
});
$('select-none').addEventListener('click', async () => {
  await put('/api/plan/selection', { selected: false, allMatching: 'all' });
  renderSummary(await get('/api/plan/summary')); grid.refresh();
});
$('export').addEventListener('click', async () => {
  const text = await post('/api/plan/export');
  const a = document.createElement('a');
  a.href = URL.createObjectURL(new Blob([text], { type: 'text/csv' })); a.download = 'convertr-plan.csv';
  document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 1000);
});

// ---------- live events ----------
function connectEvents() {
  if (events) events.close();
  events = new EventSource('/api/events');
  events.addEventListener('scan', (e) => { const s = JSON.parse(e.data); renderScan(s); if (s.state === 'done') showResults(); });
  for (const t of ['queue', 'job', 'progress', 'undo', 'log']) events.addEventListener(t, (e) => queue.onEvent(t, JSON.parse(e.data)));
  events.addEventListener('hardware', (e) => renderHardware(JSON.parse(e.data)));
  events.addEventListener('plan', () => { if (!$('results').hidden) refreshAll(); });
  events.onerror = () => { /* EventSource reconnects by itself; a 401 closes it, the next action shows the login */ };
}

// ---------- folder browser ----------
const dlg = $('browse-dialog');
let browsePath = '';
$('browse').addEventListener('click', async () => {
  $('browse-ok').disabled = true; $('browse-error').textContent = '';
  dlg.showModal();
  await browseTo($('media-folder').value.trim() || '').catch(async () => browseTo(''));
});
async function browseTo(path) {
  $('browse-error').textContent = '';
  try {
    const r = await get('/api/fs/list' + (path ? '?path=' + encodeURIComponent(path) : ''));
    browsePath = r.path;
    $('browse-path').textContent = r.path || '(allowed folders)';
    $('browse-up').disabled = !r.parent;
    $('browse-up').dataset.parent = r.parent || '';
    $('browse-ok').disabled = !r.path;
    $('browse-free').textContent = r.path && r.freeBytes ? fmtBytes(r.freeBytes) + ' free' : '';
    const ul = $('browse-list'); ul.replaceChildren();
    if (r.dirs.length === 0) { const li = document.createElement('li'); li.className = 'empty'; li.textContent = 'No sub-folders'; ul.append(li); }
    for (const d of r.dirs) { const li = document.createElement('li'); li.textContent = d.name; li.addEventListener('click', () => browseTo(d.path)); ul.append(li); }
  } catch (err) { $('browse-error').textContent = err.message; throw err; }
}
$('browse-up').addEventListener('click', () => { const p = $('browse-up').dataset.parent; if (p) browseTo(p); });
$('browse-ok').addEventListener('click', () => { $('media-folder').value = browsePath; dlg.close(); });

// ---------- setup page ----------
const FIELDS = [
  ['Container', 'Output container', 'select', ['Mp4', 'Mkv']],
  ['Mode', 'Encoder (Gpu = Intel Arc, fast; Cpu = smallest files, very slow)', 'select', ['Gpu', 'Cpu']],
  ['Kbps1080', 'Max kbps at 1080p', 'number'], ['Kbps720', 'Max kbps at 720p', 'number'],
  ['KbpsSd', 'Max kbps at SD', 'number'], ['KbpsBelowSd', 'Max kbps below SD', 'number'],
  ['KeepIfSmallerPercent', 'Keep only if smaller by (%)', 'number'], ['KeepIfSmallerMb', 'Keep only if saves at least (MB)', 'number'],
  ['ProbeThreads', 'Scan threads', 'number'], ['ParallelGpuJobs', 'Parallel GPU jobs', 'number'], ['ParallelCpuJobs', 'Parallel CPU jobs', 'number'],
  ['ProcessInterlaced', 'Deinterlace interlaced video', 'check'], ['CleanJunkMetadata', 'Clean junk metadata', 'check'],
  ['PreserveTimestamps', 'Keep file timestamps', 'check'], ['IncludePlexVersions', 'Include "Plex Versions" folders', 'check'],
];
const ENCODER_FIELDS = [
  ['UseScratch', 'Use the local scratch disk (recommended for a NAS)', 'check'],
  ['ScratchFolder', 'Scratch folder', 'text'], ['ScratchBudgetGb', 'Scratch budget GB (0 = automatic)', 'number'],
  ['MaxScratchPerJobGb', 'Largest file on scratch, GB (0 = no limit)', 'number'], ['MaxConcurrentTransfers', 'Copies at the same time', 'number'],
  ['CopyBackVerify', 'Check copy back by', 'select', ['SizeAndProbe', 'Checksum']], ['KeepScratchOnFailure', 'Keep scratch files after a failure', 'check'],
  ['GpuBackend', 'GPU backend (VAAPI = recommended)', 'select', ['Auto', 'Vaapi', 'Qsv', 'Nvenc']], ['GpuCodec', 'GPU codec (AV1 is untested)', 'select', ['Hevc', 'Av1']],
  ['IntelCq', 'Intel VAAPI HEVC quality (default 23; lower = better, bigger)', 'number'], ['IntelQsvCq', 'Intel QSV HEVC quality (default 21 = same as VAAPI 23)', 'number'],
  ['IntelAv1Cq', 'Intel AV1 quality (UNTESTED, not benchmarked)', 'number'], ['GpuDevice', 'GPU render device (empty = automatic)', 'text'],
  ['CpuCrf', 'CPU x265 CRF', 'number'], ['GpuCq', 'NVENC quality (cq)', 'number'],
];
function buildSettingsForm() {
  fillFields($('settings-fields'), FIELDS);
  fillFields($('encoder-fields'), ENCODER_FIELDS);
}
function fillFields(box, list) {
  box.replaceChildren();
  for (const [key, label, kind, opts] of list) {
    const v = settings.values[key];
    const wrap = document.createElement('label'); let input;
    if (kind === 'select') { input = document.createElement('select'); for (const o of opts) { const op = document.createElement('option'); op.value = o; op.textContent = o; input.append(op); } input.value = v; }
    else if (kind === 'number') { input = document.createElement('input'); input.type = 'number'; input.value = v; }
    else if (kind === 'text') { input = document.createElement('input'); input.type = 'text'; input.value = v || ''; input.spellcheck = false; }
    else { input = document.createElement('input'); input.type = 'checkbox'; input.checked = !!v; wrap.className = 'chk'; }
    input.dataset.key = key; input.dataset.kind = kind;
    const text = document.createElement('span'); text.textContent = label;
    if (kind === 'check') wrap.append(input, text); else wrap.append(text, input);
    box.append(wrap);
  }
}
function formBody(box) {
  const body = {};
  box.querySelectorAll('[data-key]').forEach((i) => {
    body[i.dataset.key] = i.dataset.kind === 'check' ? i.checked : i.dataset.kind === 'number' ? Number(i.value) : i.value;
  });
  return body;
}
$('setup-settings').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    settings = await put('/api/settings', formBody($('settings-fields')));
    $('settings-msg').textContent = 'Saved.';
    if (!$('results').hidden) refreshAll();
  } catch (err) { $('settings-msg').textContent = err.message; }
});
$('setup-encoder').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    settings = await put('/api/settings', formBody($('encoder-fields')));
    $('encoder-msg').textContent = 'Saved.'; loadServerInfo();
  } catch (err) { $('encoder-msg').textContent = err.message; }
});

// ---------- hardware ----------
async function loadHardware() { try { renderHardware((await get('/api/hardware')).hardware); } catch { /* shown on demand */ } }
function renderHardware(h) {
  const dl = $('hw-info'); dl.replaceChildren();
  const row = (k, val) => { const dt = document.createElement('dt'); dt.textContent = k; const dd = document.createElement('dd'); dd.textContent = val; dl.append(dt, dd); };
  if (!h || !h.detected) { row('Status', 'not detected yet'); $('hw-log').textContent = ''; return; }
  row('CPU', h.cpu || '?'); row('GPU', (h.gpus || []).join('; ') || 'none found'); row('ffmpeg', h.ffmpeg || 'not found');
  if (h.intel) {
    row('Intel GPU', h.intel.summary + (h.intel.driver ? ' (' + h.intel.driver + ')' : ''));
    if (h.intel.device) row('Render device', h.intel.device);
    if (h.intel.systemLibsNeeded) row('Libraries', 'the system libva / iHD is used (the bundled ones crash); remembered');
    if (h.intel.reason) row('Problem', h.intel.reason);
    $('hw-log').textContent = (h.intel.log || []).join('\n');
  }
  const e = h.effective || {};
  row('In use', `${e.mode} - ${e.backend} ${e.codec}${e.device ? ' on ' + e.device : ''}`);
}
$('hw-detect').addEventListener('click', async () => {
  $('hw-detect').disabled = true; $('hw-msg').textContent = 'Testing the encoders (this takes a few seconds)...';
  try { renderHardware((await post('/api/hardware/detect')).hardware); $('hw-msg').textContent = 'Done.'; settings = await get('/api/settings'); buildSettingsForm(); loadServerInfo(); }
  catch (err) { $('hw-msg').textContent = err.message; } finally { $('hw-detect').disabled = false; }
});

async function loadServerInfo() {
  const v = await get('/api/version').catch(() => null);
  const dl = $('server-info'); dl.replaceChildren();
  const row = (k, val) => { const dt = document.createElement('dt'); dt.textContent = k; const dd = document.createElement('dd'); dd.textContent = val; dl.append(dt, dd); };
  if (v) { row('Version', v.version); row('Core', v.core); row('Platform', v.platform); row('ffprobe', v.ffprobe || 'not found'); row('Allowed folders', (v.roots || []).join(', ') || 'none configured'); }
  const ro = settings.readOnly || {};
  const sv = settings.values;
  row('GPU backend', `${sv.GpuBackend} (${sv.GpuCodec})`);
  row('Scratch disk', sv.UseScratch ? (sv.ScratchFolder || 'not set') : 'off');
  if ((ro.FfmpegEnvironment || []).length) row('ffmpeg environment', ro.FfmpegEnvironment.join(' '));
}
$('password-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    await post('/api/auth/password', { current: $('pw-current').value, next: $('pw-new').value });
    $('pw-current').value = ''; $('pw-new').value = '';
    setCsrf(null); await start();
  } catch (err) { $('pw-msg').textContent = err.message; }
});

start();
