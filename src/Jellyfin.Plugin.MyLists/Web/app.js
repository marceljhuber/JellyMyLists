'use strict';
const $ = (s, r = document) => r.querySelector(s);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const app = $('#app');

function token() {
  try {
    const creds = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
    const s = (creds.Servers || []).filter((x) => x.AccessToken).sort((a, b) => (b.DateLastAccessed || 0) - (a.DateLastAccessed || 0));
    return s[0]?.AccessToken || null;
  } catch { return null; }
}
async function api(path, body, method) {
  const headers = {};
  const t = token();
  if (t) headers.Authorization = `MediaBrowser Token="${t}"`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(`api/${path}`, body !== undefined || method ? { method: method || 'POST', headers, body: body !== undefined ? JSON.stringify(body) : undefined } : { headers });
  const data = res.headers.get('content-type')?.includes('json') ? await res.json() : null;
  if (!res.ok) throw Object.assign(new Error(data?.error || `Request failed (${res.status})`), { status: res.status });
  return data;
}
let serverId = '';
fetch('../System/Info/Public').then((r) => r.json()).then((i) => { serverId = i.Id || ''; }).catch(() => {});
const details = (id) => `../web/#/details?id=${id}${serverId ? `&serverId=${serverId}` : ''}`;
const coverUrl = (f) => `cover/${f}`;
const img = (id, tag, w = 400) => `../Items/${id}/Images/Primary?fillWidth=${w}&quality=85${tag ? `&tag=${tag}` : ''}`;
let toastTimer;
function toast(msg, err) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'show' + (err ? ' err' : '');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => { t.className = ''; }, 3500);
}
const SORTS = [
  ['source', 'Original source order'], ['manual', 'Saved order'], ['release_asc', 'Release date ↑'], ['release_desc', 'Release date ↓'],
  ['title', 'Title'], ['rating', 'Community rating'], ['runtime', 'Runtime'], ['unwatched_first', 'Unwatched first'],
];
const PROMPT = `Convert the movie list below into plain text for import.
Rules:
- One movie per line, in the original order.
- Format exactly: Title (Year) - the original/English title and the release year, e.g. Pulp Fiction (1994)
- No numbering, bullets, quotes, headings, comments or other text.
- Skip anything that is not a movie. If a title is unclear, leave it out instead of guessing.
- Put the result in a single code block.

Here is the list:

`;
const PROMPT_HTML = `<details class="help"><summary>Have a list as text, a web page or a screenshot? Let ChatGPT convert it</summary><p class="dim">1) Copy the prompt  2) paste it into ChatGPT and add your dump (text or screenshot) below it  3) paste the answer here and press <b>Check</b>.</p><pre>${PROMPT.trim()}</pre><button type="button" class="btn" data-copy>Copy prompt</button></details>`;
const MDB_PRESETS = [['IMDb Top 250', 'https://mdblist.com/lists/snoak/imdb-top-250-movies'], ['IMDb MovieMeter Top 100', 'https://mdblist.com/lists/linaspurinis/imdb-moviemeter-top-100']];
const SOURCE_LABEL = { manual: 'Manual list', rule: 'Rule list', csv: 'Imported (CSV)', letterboxd: 'Letterboxd', tmdb: 'TMDB', mdblist: 'MDBList' };

function copyPrompt() {
  const done = () => toast('Prompt copied');
  if (navigator.clipboard?.writeText) navigator.clipboard.writeText(PROMPT).then(done, fallback); else fallback();
  function fallback() { const t = document.createElement('textarea'); t.value = PROMPT; document.body.append(t); t.select(); try { document.execCommand('copy'); done(); } catch { toast('Copy failed: select the prompt text manually', true); } t.remove(); }
}

async function checkText(ta, box) {
  if (!ta.value.trim()) { box.innerHTML = '<p class="dim">Paste something first.</p>'; return; }
  box.innerHTML = '<p class="dim">Checking…</p>';
  try {
    const rows = await api('preview', { text: ta.value });
    const hit = rows.filter((r) => r.match).length;
    box.innerHTML = `<p><b>${rows.length}</b> lines understood · <span style="color:var(--ok)">${hit} in your library</span> · ${rows.length - hit} not in your library</p>
      <div class="prev">${rows.map((r) => `<div class="${r.match ? 'ok' : 'no'}"><span>${r.match ? '✓' : '–'}</span> ${esc(r.title)}${r.year ? ` (${r.year})` : ''}${r.match && r.match.toLowerCase() !== `${r.title} (${r.year})`.toLowerCase() ? ` <small class="dim">→ ${esc(r.match)}</small>` : ''}</div>`).join('')}</div>`;
  } catch (e) { box.innerHTML = `<p style="color:var(--danger)">${esc(e.message)}</p>`; }
}

function sortEntries(entries, mode) {
  const e = [...entries];
  const by = (f, dir = 1) => e.sort((a, b) => { const x = f(a), y = f(b); return x == null ? 1 : y == null ? -1 : (x < y ? -dir : x > y ? dir : 0); });
  switch (mode) {
    case 'manual': return by((x) => x.manualPos ?? x.sourcePos);
    case 'release_asc': return by((x) => x.premiere ?? (x.year ? `${x.year}` : null));
    case 'release_desc': return by((x) => x.premiere ?? (x.year ? `${x.year}` : null), -1);
    case 'title': return by((x) => x.title.toLowerCase());
    case 'rating': return by((x) => x.rating, -1);
    case 'runtime': return by((x) => x.runtimeMin);
    case 'unwatched_first': return e.sort((a, b) => (a.played - b.played) || (a.sourcePos - b.sourcePos));
    default: return by((x) => x.sourcePos);
  }
}

// ---------------------------------------------------------------- overview
async function overview() {
  app.innerHTML = '<p class="dim">Loading…</p>';
  let lists;
  try { lists = await api('lists'); } catch (e) { return needLogin(e); }
  app.innerHTML = `<div class="head"><h2>My Lists</h2><div class="row"><button class="btn primary" id="new">+ New list</button></div></div>
    ${lists.length ? '' : '<div class="empty">No lists yet. Create one: a rule (e.g. all films by a director), an IMDb/Letterboxd CSV import, or pick movies by hand.</div>'}
    <div class="lists">${lists.map((l) => `<a class="lcard" href="#/list/${l.id}">
      <div class="covers ${l.cover ? 'single' : ''}">${l.cover ? `<img loading="lazy" src="${coverUrl(l.cover)}" alt="">` : l.covers.map((c) => `<img loading="lazy" src="${img(c, '', 200)}" alt="">`).join('')}</div>
      <div class="meta"><b>${esc(l.name)}</b>
      <span class="dim">${l.available}${l.available < l.total ? ` of ${l.total}` : ''} ${l.total === 1 ? 'movie' : 'movies'} · ${l.watched} watched</span>
      <div class="bar2"><i style="width:${l.available ? Math.round(100 * l.watched / l.available) : 0}%"></i></div>
      <div class="dim" style="font-size:12px;margin-top:6px">${SOURCE_LABEL[l.sourceType] || l.sourceType}</div></div></a>`).join('')}</div>`;
  $('#new').onclick = createDialog;
}

function needLogin(e) {
  app.innerHTML = e.status === 401
    ? `<div class="empty"><p>Sign in to Jellyfin first.</p><a class="btn primary" href="../web/#/login.html">Open Jellyfin</a></div>`
    : `<div class="empty">${esc(e.message)}</div>`;
}

function createDialog() {
  const d = document.createElement('dialog');
  let type = 'manual';
  d.innerHTML = `<h2>New list</h2>
    <div class="field"><label>Name</label><input id="n" placeholder="Tarantino"></div>
    <div class="field"><label>Source</label><div class="tabs" id="tabs">
      <button class="btn on" data-t="manual">Empty</button><button class="btn" data-t="rule">Rule</button>
      <button class="btn" data-t="mdblist">MDBList</button><button class="btn" data-t="csv">CSV / text</button><button class="btn" data-t="letterboxd">Letterboxd URL</button><button class="btn" data-t="tmdb">TMDB list</button></div></div>
    <div id="body"></div>
    <div class="field"><label>Default sorting</label><select id="s">${SORTS.map(([v, n]) => `<option value="${v}">${n}</option>`).join('')}</select></div>
    <div class="row end"><button class="btn" id="c">Cancel</button><button class="btn primary" id="ok">Create</button></div>`;
  document.body.append(d); d.showModal();
  const body = $('#body', d);
  const render = () => {
    body.innerHTML = {
      manual: '<p class="dim">Add movies from your library afterwards.</p>',
      rule: `<div class="field"><label>Director</label><input id="rd" placeholder="Quentin Tarantino"></div>
        <div class="field"><label>Actor (optional, combined with director)</label><input id="ra"></div>
        <div class="row"><div class="grow field"><label>Genre (optional)</label><input id="rg" placeholder="Horror"></div>
        <div class="field"><label>From year</label><input id="ry1" type="number" style="width:100px"></div><div class="field"><label>To year</label><input id="ry2" type="number" style="width:100px"></div></div>
        <p class="dim">Fills itself from your library and updates when you add films.</p>`,
      csv: `<div class="field"><label>Paste an IMDb list export, a Letterboxd list CSV, or one title / IMDb id per line</label><textarea id="tx" placeholder="tt0110912&#10;Pulp Fiction (1994)&#10;Jackie Brown, 1997"></textarea></div>
        <div class="row"><input type="file" id="f" accept=".csv,.txt" class="grow"><button type="button" class="btn" data-check>Check</button></div><div id="chk"></div>
        ${PROMPT_HTML}<p class="dim">IMDb: open the list → ⋯ → Export. Letterboxd: list page → ⋯ → Export list as CSV. (Both sites block automatic fetching, so the file route is the reliable one.)</p>`,
      mdblist: `<div class="field"><label>MDBList list URL (public, no account or key needed)</label><input id="u" placeholder="https://mdblist.com/lists/snoak/imdb-top-250-movies"></div>
        <div class="chips">${MDB_PRESETS.map(([n, u]) => `<button type="button" class="btn" data-mdb="${u}" data-name="${n}">${n}</button>`).join('')}</div>
        <p class="dim">Keeps the original order and refreshes itself daily. Find more lists at mdblist.com/toplists (open one → copy the URL). Only movies are used.</p>`,
      letterboxd: `<div class="field"><label>Public Letterboxd list URL</label><input id="u" placeholder="https://letterboxd.com/user/list/name/"></div><p class="dim">Best effort: Letterboxd may refuse server requests. Then use CSV.</p>`,
      tmdb: `<div class="field"><label>TMDB list URL</label><input id="u" placeholder="https://www.themoviedb.org/list/8267"></div><p class="dim">Needs a TMDB API key in the plugin settings.</p>`,
    }[type];
    const f = $('#f', d); if (f) f.onchange = async () => { $('#tx', d).value = await f.files[0].text(); };
  };
  render();
  d.addEventListener('click', (ev) => {
    if (ev.target.closest('[data-copy]')) copyPrompt();
    const m = ev.target.closest('[data-mdb]'); if (m) { $('#u', d).value = m.dataset.mdb; if (!$('#n', d).value) $('#n', d).value = m.dataset.name; }
    if (ev.target.closest('[data-check]')) checkText($('#tx', d), $('#chk', d));
  });
  $('#tabs', d).onclick = (e) => { const b = e.target.closest('button'); if (!b) return; type = b.dataset.t; d.querySelectorAll('#tabs button').forEach((x) => x.classList.toggle('on', x === b)); render(); };
  $('#c', d).onclick = () => { d.close(); d.remove(); };
  $('#ok', d).onclick = async () => {
    const num = (id) => ($(id, d)?.value ? parseInt($(id, d).value, 10) : null);
    const req = { name: $('#n', d).value, sourceType: type, defaultSort: $('#s', d).value };
    if (type === 'rule') req.rule = { director: $('#rd', d).value || null, actor: $('#ra', d).value || null, genre: $('#rg', d).value || null, yearFrom: num('#ry1'), yearTo: num('#ry2') };
    if (type === 'csv') req.text = $('#tx', d).value;
    if (type === 'letterboxd' || type === 'tmdb' || type === 'mdblist') req.sourceUrl = $('#u', d).value;
    $('#ok', d).disabled = true; $('#ok', d).textContent = 'Working…';
    try { const l = await api('lists', req); d.close(); d.remove(); location.hash = `#/list/${l.id}`; }
    catch (e) { toast(e.message, true); $('#ok', d).disabled = false; $('#ok', d).textContent = 'Create'; }
  };
}

// ---------------------------------------------------------------- list view
async function listView(id) {
  app.innerHTML = '<p class="dim">Loading…</p>';
  let data;
  try { data = await api(`lists/${id}`); } catch (e) { return e.status === 404 ? (location.hash = '#/') : needLogin(e); }
  const { list } = data; let entries = data.entries;
  let sort = list.defaultSort; let hideWatched = false; let dirty = false; let order = null;

  const draw = () => {
    const inLib = entries.filter((e) => e.inLibrary);
    const missing = entries.filter((e) => !e.inLibrary);
    const watched = inLib.filter((e) => e.played).length;
    let shown = order ? order.map((k) => inLib.find((e) => e.key === k)).filter(Boolean) : sortEntries(inLib, sort);
    if (hideWatched) shown = shown.filter((e) => !e.played);
    const showPos = sort === 'source' || sort === 'manual' || order;
    const canDrag = list.sourceType !== 'rule' || true;
    app.innerHTML = `<div class="head"><h2>${esc(list.name)}</h2>
        <div class="dim">${inLib.length} ${inLib.length === 1 ? 'movie' : 'movies'} · ${watched} watched · ${SOURCE_LABEL[list.sourceType] || ''}${missing.length ? ` · ${missing.length} not in your library` : ''}</div>
        <div class="row">
          ${list.sourceType !== 'rule' ? '<button class="btn primary" id="add">+ Add movies</button>' : ''}
          <select id="sort" style="width:auto">${SORTS.map(([v, n]) => `<option value="${v}" ${!order && v === sort ? 'selected' : ''}>${n}</option>`).join('')}${order ? '<option selected>Custom (unsaved)</option>' : ''}</select>
          <label class="row" style="gap:6px"><input type="checkbox" id="hw" style="width:auto" ${hideWatched ? 'checked' : ''}> Hide watched</label>
          ${dirty || order ? '<button class="btn primary" id="save">Save this order</button>' : (sort !== list.defaultSort ? '<button class="btn" id="def">Use as default sorting</button>' : '')}
          <button class="btn" id="edit">Edit</button>
        </div></div>
      ${inLib.length ? `<div class="grid" id="grid">${shown.map((e, i) => `<a class="poster ${e.played ? 'watched' : ''}" data-k="${e.key}" draggable="${canDrag}" href="${details(e.itemId)}">
          ${showPos ? `<span class="pos">${i + 1}</span>` : ''}
          <div class="img" style="background-image:url('${img(e.itemId, e.imageTag)}')"></div>
          <button class="x" data-rm="${e.key}" title="Remove from list">✕</button>
          <div class="t">${esc(e.title)}<small>${e.year ?? ''}</small></div></a>`).join('')}</div>` : `<div class="empty">${list.sourceType === 'rule' ? 'No movie in your library matches this rule.' : 'This list is empty.'}</div>`}
      ${missing.length ? `<details class="missing"><summary>${missing.length} not in your library</summary><ul>${missing.map((e) => `<li>${esc(e.title)} ${e.year ? `(${e.year})` : ''} ${list.sourceType !== 'rule' ? `<button class="btn danger" style="padding:0 8px" data-rm="${e.key}">remove</button>` : ''}</li>`).join('')}</ul></details>` : ''}`;
    $('#sort').onchange = (ev) => { sort = ev.target.value; order = null; dirty = false; draw(); };
    $('#hw').onchange = (ev) => { hideWatched = ev.target.checked; draw(); };
    $('#edit').onclick = () => editDialog(list, () => listView(id));
    const add = $('#add'); if (add) add.onclick = () => addDialog(list, () => listView(id));
    const save = $('#save'); if (save) save.onclick = async () => {
      const keys = (order || sortEntries(inLib, sort).map((e) => e.key));
      await api(`lists/${id}/order`, { keys }); await api(`lists/${id}`, { defaultSort: 'manual' });
      toast('Order saved'); listView(id);
    };
    const def = $('#def'); if (def) def.onclick = async () => { await api(`lists/${id}`, { defaultSort: sort }); list.defaultSort = sort; toast('Default sorting saved'); draw(); };
    app.querySelectorAll('[data-rm]').forEach((b) => { b.onclick = async (ev) => { ev.preventDefault(); ev.stopPropagation(); await api(`lists/${id}/entries/${b.dataset.rm}`, undefined, 'DELETE'); entries = entries.filter((e) => e.key !== b.dataset.rm); order = order && order.filter((k) => k !== b.dataset.rm); draw(); }; });
    const grid = $('#grid'); if (grid) dragSort(grid, (keys) => { order = keys; draw(); });
  };
  draw();
}

function dragSort(grid, onChange) {
  let dragging = null;
  grid.addEventListener('dragstart', (e) => { dragging = e.target.closest('.poster'); dragging?.classList.add('drag'); e.dataTransfer.effectAllowed = 'move'; });
  grid.addEventListener('dragend', () => { dragging?.classList.remove('drag'); dragging = null; });
  grid.addEventListener('dragover', (e) => {
    e.preventDefault();
    const over = e.target.closest('.poster');
    if (!dragging || !over || over === dragging) return;
    const r = over.getBoundingClientRect();
    grid.insertBefore(dragging, e.clientX > r.left + r.width / 2 ? over.nextSibling : over);
  });
  grid.addEventListener('drop', (e) => { e.preventDefault(); onChange([...grid.querySelectorAll('.poster')].map((p) => p.dataset.k)); });
}

function editDialog(list, done) {
  const d = document.createElement('dialog');
  const canSync = ['rule', 'letterboxd', 'tmdb', 'mdblist'].includes(list.sourceType);
  d.innerHTML = `<h2>Edit list</h2>
    <div class="field"><label>Name</label><input id="n" value="${esc(list.name)}"></div>
    ${list.sourceType === 'rule' ? `<div class="row"><div class="grow field"><label>Director</label><input id="rd" value="${esc(list.rule?.director || '')}"></div><div class="grow field"><label>Actor</label><input id="ra" value="${esc(list.rule?.actor || '')}"></div></div>
      <div class="row"><div class="grow field"><label>Genre</label><input id="rg" value="${esc(list.rule?.genre || '')}"></div>
      <div class="field"><label>From</label><input id="ry1" type="number" style="width:90px" value="${list.rule?.yearFrom ?? ''}"></div><div class="field"><label>To</label><input id="ry2" type="number" style="width:90px" value="${list.rule?.yearTo ?? ''}"></div></div>` : ''}
    ${list.sourceType !== 'rule' ? `<div class="field"><label>Add more via CSV / text (appends)</label><textarea id="tx" style="min-height:70px"></textarea>${PROMPT_HTML}<div class="row" style="margin-top:6px"><button type="button" class="btn" data-check>Check</button><button class="btn" id="imp">Import</button></div><div id="chk"></div></div>` : ''}
    <div class="field"><label>Cover image</label><div class="coverrow">${list.cover ? `<img src="${coverUrl(list.cover)}" alt="">` : '<span class="dim">Automatic (poster collage)</span>'}<div><input type="file" id="cv" accept="image/png,image/jpeg,image/webp">${list.cover ? '<button class="btn" id="cvx" style="margin-top:6px">Back to automatic</button>' : ''}</div></div></div>
    ${canSync ? '<div class="field"><button class="btn" id="sync">Re-sync from source</button></div>' : ''}
    <div class="row end"><button class="btn danger" id="del">Delete list</button><span class="grow"></span><button class="btn" id="c">Close</button><button class="btn primary" id="ok">Save</button></div>`;
  document.body.append(d); d.showModal();
  const close = () => { d.close(); d.remove(); };
  $('#c', d).onclick = () => { close(); done(); };
  $('#del', d).onclick = async () => { if (!confirm(`Delete "${list.name}"? Your movies are not touched.`)) return; await api(`lists/${list.id}`, undefined, 'DELETE'); close(); location.hash = '#/'; };
  $('#ok', d).onclick = async () => {
    const num = (id) => ($(id, d)?.value ? parseInt($(id, d).value, 10) : null);
    const body = { name: $('#n', d).value };
    if (list.sourceType === 'rule') body.rule = { director: $('#rd', d).value || null, actor: $('#ra', d).value || null, genre: $('#rg', d).value || null, yearFrom: num('#ry1'), yearTo: num('#ry2') };
    await api(`lists/${list.id}`, body); close(); done();
  };
  const sync = $('#sync', d); if (sync) sync.onclick = async () => { sync.disabled = true; try { await api(`lists/${list.id}/sync`, {}); toast('Synced'); close(); done(); } catch (e) { toast(e.message, true); sync.disabled = false; } };
  const imp = $('#imp', d); if (imp) imp.onclick = async () => { try { await api(`lists/${list.id}/import`, { text: $('#tx', d).value }); toast('Imported'); close(); done(); } catch (e) { toast(e.message, true); } };
  d.addEventListener('click', (ev) => { if (ev.target.closest('[data-copy]')) copyPrompt(); if (ev.target.closest('[data-check]')) checkText($('#tx', d), $('#chk', d)); });
  const cv = $('#cv', d); if (cv) cv.onchange = async () => {
    const f = cv.files[0]; if (!f) return;
    try {
      const t = token(); const res = await fetch(`api/lists/${list.id}/cover`, { method: 'POST', headers: { ...(t ? { Authorization: `MediaBrowser Token="${t}"` } : {}), 'Content-Type': f.type }, body: f });
      const data = await res.json(); if (!res.ok) throw new Error(data.error || 'Upload failed');
      toast('Cover saved'); close(); done();
    } catch (e) { toast(e.message, true); }
  };
  const cvx = $('#cvx', d); if (cvx) cvx.onclick = async () => { await api(`lists/${list.id}/cover`, undefined, 'DELETE'); toast('Cover removed'); close(); done(); };
}

function addDialog(list, done) {
  const d = document.createElement('dialog');
  const added = new Set(list.keysInList || []);
  d.innerHTML = `<h2>Add movies to “${esc(list.name)}”</h2>
    <div class="field"><input id="q" placeholder="Search your library…" autocomplete="off"></div>
    <div id="res" class="sresults"></div>
    <div class="row end" style="margin-top:14px"><button class="btn primary" id="done">Done</button></div>`;
  document.body.append(d); d.showModal();
  const q = $('#q', d), res = $('#res', d); q.focus();
  const finish = () => { d.close(); d.remove(); done(); };
  $('#done', d).onclick = finish; d.addEventListener('cancel', (e) => { e.preventDefault(); finish(); });
  let t;
  q.oninput = () => { clearTimeout(t); t = setTimeout(async () => {
    const r = await api(`search?q=${encodeURIComponent(q.value)}`).catch(() => []);
    res.innerHTML = r.length ? r.map((m) => `<button class="sres ${added.has(m.itemId) ? 'added' : ''}" data-i="${m.itemId}"><div class="img" style="background-image:url('${img(m.itemId, '', 200)}')"></div><small>${esc(m.title)} ${m.year ? `(${m.year})` : ''}</small></button>`).join('') : (q.value.length > 1 ? '<p class="dim">No match in your library.</p>' : '');
    res.querySelectorAll('.sres').forEach((b) => { b.onclick = async () => { if (b.classList.contains('added')) return; try { await api(`lists/${list.id}/entries`, { itemId: b.dataset.i }); added.add(b.dataset.i); b.classList.add('added'); } catch (e) { toast(e.message, true); } }; });
  }, 250); };
}

// ---------------------------------------------------------------- router
async function route() {
  try { const s = await api('status'); $('#who').textContent = s.user || ''; } catch { /* ignore */ }
  const m = location.hash.match(/^#\/list\/(\w+)/);
  m ? listView(m[1]) : overview();
}
window.addEventListener('hashchange', route);
route();
