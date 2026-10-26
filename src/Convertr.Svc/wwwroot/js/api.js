// Copyright (C) 2026 JeepNWilly. Convertr_svc is free software under the GNU GPL v3 or later; no warranty.
let csrf = null;
export const setCsrf = (t) => { csrf = t; };

export async function api(method, url, body) {
  const headers = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (csrf && method !== 'GET') headers['X-CSRF-Token'] = csrf;
  const res = await fetch(url, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin' });
  let data = null;
  const type = res.headers.get('content-type') || '';
  if (type.includes('json')) data = await res.json().catch(() => null);
  else if (res.ok) data = await res.text();
  if (!res.ok) {
    const err = new Error((data && data.error) || `Request failed (${res.status})`);
    err.status = res.status; err.data = data;
    throw err;
  }
  return data;
}
export const get = (u) => api('GET', u);
export const post = (u, b) => api('POST', u, b ?? {});
export const put = (u, b) => api('PUT', u, b);
export const del = (u) => api('DELETE', u);
