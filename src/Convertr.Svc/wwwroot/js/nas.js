// Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU GPL v3 or later; no warranty.
// Setup page: Network storage (NAS). Commands are only shown (never run). Everything from the server is put in with textContent.
import { get, post } from './api.js';

const $ = (id) => document.getElementById(id);
let opts = { mediaFolder: () => '' };
let loaded = false;
let lastCmds = null;

export function init(o) {
  opts = { ...opts, ...o };
  if (loaded) return;
  loaded = true;
  $('nas-list').addEventListener('click', listExports);
  $('nas-gen').addEventListener('click', showCommands);
  $('nas-check').addEventListener('click', checkMount);
  $('nas-copy-all').addEventListener('click', () => copyText($('nas-script').textContent, $('nas-copy-msg')));
  document.querySelectorAll('[data-copy-from]').forEach((b) => b.addEventListener('click', () => copyText($(b.dataset.copyFrom).textContent, $('nas-copy-msg'))));
}

export async function load() {
  try {
    const i = await get('/api/nas');
    const c = i.config || {};
    if (!$('nas-host').value) $('nas-host').value = c.host || '';
    if (!$('nas-share').value) $('nas-share').value = c.share || '';
    if (!$('nas-mp').value) $('nas-mp').value = c.mountPoint || '/mnt/nas-media';
    if (!$('nas-media').value) {
      const mp = $('nas-mp').value; const cand = c.mediaFolder || opts.mediaFolder() || '';
      $('nas-media').value = cand === mp || cand.startsWith(mp + '/') ? cand : '';
    }
    $('nas-vers').value = c.vers || '3'; $('nas-ro').checked = !!c.readOnly;
    const hint = $('nas-hint');
    hint.hidden = !i.installHint; hint.textContent = i.installHint || '';
    $('nas-server-ip').textContent = (i.serverAddresses || []).join(' or ') || "this server's address";
  } catch (err) { $('nas-error').textContent = err.message; }
}

const form = () => ({
  host: $('nas-host').value.trim(), share: $('nas-share').value.trim(), mountPoint: $('nas-mp').value.trim(),
  vers: $('nas-vers').value, readOnly: $('nas-ro').checked, mediaFolder: $('nas-media').value.trim(),
});

export async function copyText(text, msgEl) {
  let ok = false;
  try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; } } catch { /* fall through */ }
  if (!ok) {
    // plain http on a LAN address is not a "secure context": the clipboard API is missing, use the old way
    const ta = document.createElement('textarea'); ta.value = text; ta.className = 'offscreen'; ta.setAttribute('readonly', '');
    document.body.append(ta); ta.select();
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    ta.remove();
  }
  if (msgEl) msgEl.textContent = ok ? 'Copied.' : 'Could not copy: select the text and copy it by hand.';
  return ok;
}

function cmdBlock(command, label, note) {
  const li = document.createElement('li');
  if (label) { const w = document.createElement('div'); w.className = 'what'; w.textContent = label; li.append(w); }
  const box = document.createElement('div'); box.className = 'cmd';
  const pre = document.createElement('pre'); pre.textContent = command; pre.tabIndex = 0;
  const b = document.createElement('button'); b.type = 'button'; b.className = 'copy'; b.textContent = 'Copy';
  b.addEventListener('click', () => copyText(command, $('nas-copy-msg')));
  box.append(pre, b); li.append(box);
  if (note) { const n = document.createElement('div'); n.className = 'note'; n.textContent = note; li.append(n); }
  return li;
}

async function listExports() {
  $('nas-error').textContent = ''; $('nas-msg').textContent = 'Asking the NAS...'; $('nas-exports').hidden = true;
  $('nas-list').disabled = true;
  try {
    const r = await post('/api/nas/exports', { host: $('nas-host').value.trim() });
    const body = $('nas-exports-body'); body.replaceChildren();
    if (!r.ok) {
      $('nas-msg').textContent = '';
      $('nas-error').textContent = r.error || 'The NAS did not answer.';
      if (r.hint) { $('nas-hint').hidden = false; $('nas-hint').textContent = r.error + ' Run once: ' + r.hint; }
      return;
    }
    $('nas-msg').textContent = r.note || (r.exports.length + ' share' + (r.exports.length === 1 ? '' : 's') + ' found.');
    for (const e of r.exports) {
      const tr = document.createElement('tr');
      const t = (txt) => { const td = document.createElement('td'); td.textContent = txt; tr.append(td); return td; };
      t(e.path); t(e.clients || '');
      t(e.allowsThisServer === true ? 'allowed' : e.allowsThisServer === false ? 'NOT in the list' : 'unclear (host names)');
      const act = document.createElement('td'); const b = document.createElement('button'); b.type = 'button'; b.textContent = 'Use this share'; b.disabled = !e.usable;
      b.addEventListener('click', () => { $('nas-share').value = e.path; });
      act.append(b); tr.append(act); body.append(tr);
    }
    $('nas-exports').hidden = false;
  } catch (err) { $('nas-msg').textContent = ''; $('nas-error').textContent = err.message; }
  finally { $('nas-list').disabled = false; }
}

async function showCommands() {
  $('nas-error').textContent = ''; $('nas-msg').textContent = '';
  try {
    const r = await post('/api/nas/commands', form());
    lastCmds = r;
    $('nas-fstab').textContent = r.fstabLine;
    const steps = $('nas-steps'); steps.replaceChildren();
    r.steps.forEach((s, i) => steps.append(cmdBlock(s.command, `${i + 1}. ${s.title}`, s.note)));
    $('nas-script').textContent = r.script;
    const roots = r.allowedRoots;
    $('nas-roots-msg').textContent = roots.message + (roots.rewritesPathsConf ? ' Note: the installer rewrites the service\'s writable-folder list from the --media-root options, so the command repeats every folder that is already allowed.' : '');
    const rc = $('nas-roots-cmds'); rc.replaceChildren();
    roots.commands.forEach((c) => rc.append(cmdBlock(c, '', '')));
    $('nas-cmds').hidden = false;
    $('nas-msg').textContent = 'Saved. Run the commands in order, then press "Check mount".';
  } catch (err) { $('nas-error').textContent = err.message; }
}

const LABEL = { ok: 'OK', warn: 'Note', fail: 'Problem', skip: 'Skipped' };
async function checkMount() {
  $('nas-error').textContent = ''; $('nas-check-msg').textContent = 'Checking (can take up to 45 seconds if the NAS is slow)...';
  $('nas-check').disabled = true; $('nas-summary').hidden = true; $('nas-results').replaceChildren();
  try {
    const r = await post('/api/nas/check', { mountPoint: $('nas-mp').value.trim(), mediaFolder: $('nas-media').value.trim() });
    $('nas-check-msg').textContent = '';
    const sum = $('nas-summary'); sum.hidden = false; sum.textContent = r.summary;
    sum.className = 'summary ' + (!r.ok ? 'fail' : r.steps.some((s) => s.status === 'warn') ? 'warn' : 'ok');
    const ul = $('nas-results');
    for (const s of r.steps) {
      const li = document.createElement('li'); li.className = s.status;
      const b = document.createElement('span'); b.className = 'badge-s'; b.textContent = LABEL[s.status] || s.status;
      const t = document.createElement('strong'); t.textContent = s.title + ': ';
      const m = document.createElement('span'); m.textContent = s.message;
      li.append(b, t, m);
      if (s.commands && s.commands.length) {
        const l = document.createElement('ul'); l.className = 'cmdlist';
        s.commands.forEach((c) => l.append(cmdBlock(c, '', '')));
        li.append(l);
      }
      ul.append(li);
    }
  } catch (err) { $('nas-check-msg').textContent = ''; $('nas-error').textContent = err.message; }
  finally { $('nas-check').disabled = false; }
}
