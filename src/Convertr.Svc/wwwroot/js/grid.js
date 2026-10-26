// Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU GPL v3 or later; no warranty.
// A virtual-scrolling table: only the visible rows exist in the page; rows are fetched from the server in pages.
import { get } from './api.js';

const ROW = 34, PAGE = 100;
const COLS = [
  { key: '', label: '' },
  { key: 'name', label: 'File' }, { key: 'size', label: 'Size', num: true }, { key: 'codec', label: 'Format' },
  { key: 'res', label: 'Resolution' }, { key: 'kbps', label: 'kbps', num: true, nosort: true },
  { key: 'action', label: 'Action' }, { key: 'saving', label: 'Est. saving', num: true }, { key: '', label: 'Reason' },
];
const RES = { K4: '4K', P1080: '1080p', P720: '720p', P576: '576p', P480: '480p', Below480: '<480p', Unknown: '?' };
const fmtBytes = (b) => { if (!b) return '-'; const u = ['B', 'KB', 'MB', 'GB', 'TB']; let i = 0, v = b; while (v >= 1024 && i < 4) { v /= 1024; i++; } return (i >= 3 ? v.toFixed(1) : Math.round(v)) + ' ' + u[i]; };

export class Grid {
  constructor({ head, body, space, onToggle }) {
    this.head = head; this.body = body; this.space = space; this.onToggle = onToggle;
    this.filter = 'all'; this.q = ''; this.sort = 'name'; this.desc = false;
    this.total = 0; this.pages = new Map(); this.pending = new Set(); this.epoch = 0; this.rowsEl = new Map();
    this.buildHead();
    this.body.addEventListener('scroll', () => this.render());
    window.addEventListener('resize', () => this.render());
  }

  buildHead() {
    this.head.replaceChildren();
    COLS.forEach((c) => {
      const d = document.createElement('div'); d.textContent = c.label; if (c.num) d.className = 'num';
      if (c.key && !c.nosort) {
        d.classList.add('h'); d.setAttribute('role', 'columnheader'); d.tabIndex = 0;
        const go = () => { if (this.sort === c.key) this.desc = !this.desc; else { this.sort = c.key; this.desc = c.key === 'size' || c.key === 'saving'; } this.reset(); };
        d.addEventListener('click', go); d.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); go(); } });
        d.dataset.key = c.key;
      }
      this.head.append(d);
    });
    this.markSort();
  }

  markSort() {
    this.head.querySelectorAll('.h').forEach((h) => {
      if (h.dataset.key === this.sort) h.setAttribute('aria-sort', this.desc ? 'descending' : 'ascending'); else h.removeAttribute('aria-sort');
    });
  }

  reset(filter, q) {
    if (filter !== undefined) this.filter = filter;
    if (q !== undefined) this.q = q;
    this.markSort();
    this.pages.clear(); this.pending.clear(); this.epoch++; this.body.scrollTop = 0;
    this.total = 0; this.render(); this.ensure(0, true);
  }

  refresh() { this.pages.clear(); this.pending.clear(); this.epoch++; this.ensure(Math.floor(this.body.scrollTop / ROW), true); }

  async ensure(first, force) {
    const visible = Math.ceil(this.body.clientHeight / ROW) + 2;
    const p0 = Math.floor(Math.max(0, first - PAGE / 2) / PAGE), p1 = Math.floor((first + visible + PAGE / 2) / PAGE);
    for (let p = p0; p <= p1; p++) {
      if (this.pages.has(p) || this.pending.has(p)) continue;
      if (p > 0 && this.total && p * PAGE >= this.total) continue;
      this.pending.add(p);
      const epoch = this.epoch;
      const qs = new URLSearchParams({ filter: this.filter, sort: this.sort, dir: this.desc ? 'desc' : 'asc', skip: p * PAGE, take: PAGE });
      if (this.q) qs.set('q', this.q);
      get('/api/plan?' + qs).then((r) => {
        if (epoch !== this.epoch) return;
        this.pending.delete(p); this.pages.set(p, r.rows);
        if (this.total !== r.total) { this.total = r.total; }
        this.render();
      }).catch(() => { this.pending.delete(p); });
    }
    if (force) this.render();
  }

  row(i) { const page = this.pages.get(Math.floor(i / PAGE)); return page ? page[i % PAGE] : null; }

  render() {
    this.space.style.height = this.total * ROW + 'px';
    const first = Math.max(0, Math.floor(this.body.scrollTop / ROW) - 3);
    const last = Math.min(this.total - 1, first + Math.ceil(this.body.clientHeight / ROW) + 6);
    for (const [i, el] of this.rowsEl) if (i < first || i > last) { el.remove(); this.rowsEl.delete(i); }
    for (let i = first; i <= last; i++) {
      const r = this.row(i);
      let el = this.rowsEl.get(i);
      if (!r) { if (el) { el.remove(); this.rowsEl.delete(i); } continue; }
      if (!el || el.dataset.id !== String(r.id) || el.dataset.sel !== String(r.selected)) {
        const fresh = this.makeRow(r); fresh.style.top = i * ROW + 'px';
        if (el) el.replaceWith(fresh); else this.space.append(fresh);
        this.rowsEl.set(i, fresh);
      }
    }
    this.ensure(first);
    if (this.total === 0 && this.pending.size === 0 && this.pages.size > 0) this.showEmpty(true); else this.showEmpty(false);
  }

  showEmpty(on) {
    let e = this.body.querySelector('.empty-msg');
    if (on && !e) { e = document.createElement('p'); e.className = 'empty-msg muted'; e.textContent = 'No files match.'; e.style.padding = '12px'; this.body.append(e); }
    if (!on && e) e.remove();
  }

  makeRow(r) {
    const el = document.createElement('div');
    el.className = 'grid-row' + (r.selected ? ' sel' : ''); el.setAttribute('role', 'row');
    el.dataset.id = r.id; el.dataset.sel = r.selected;
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = r.selected; cb.disabled = !r.writes;
    cb.title = r.writes ? 'Select for conversion' : 'Nothing to convert';
    cb.setAttribute('aria-label', 'Select ' + r.name);
    cb.addEventListener('change', async () => {
      cb.disabled = true;
      try { await this.onToggle(r.id, cb.checked); r.selected = cb.checked; el.classList.toggle('sel', cb.checked); el.dataset.sel = String(cb.checked); }
      catch { cb.checked = !cb.checked; }
      cb.disabled = !r.writes;
    });
    const cell = (text, cls, title) => { const d = document.createElement('div'); d.textContent = text; if (cls) d.className = cls; if (title) d.title = title; return d; };
    const name = cell(r.path, '', r.path);
    const res = r.width ? `${RES[r.resolution] || r.resolution}` : '?';
    const act = cell(r.actionLabel + (r.warnings ? ' !' : ''), 'act-' + r.action);
    const save = cell(r.estSaveBytes > 0 ? '~' + fmtBytes(r.estSaveBytes) : r.likelyRefused ? 'would be refused' : '-', 'num' + (r.likelyRefused ? ' refused' : ''));
    el.append(cb, name, cell(fmtBytes(r.sizeBytes), 'num'), cell(r.container + ' / ' + (r.codec || '?')), cell(res), cell(r.kbps ? String(r.kbps) : '-', 'num'), act, save, cell(r.reason, 'muted', r.reason));
    // column order: checkbox, file, size, container/codec, resolution, kbps, action, saving, reason
    return el;
  }
}
