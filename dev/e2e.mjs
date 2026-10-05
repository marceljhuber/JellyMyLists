// End-to-end test against the demo server (dev/demo-server.sh). It creates its own lists and deletes them again.
//   CHROME=<path to chrome> node dev/e2e.mjs [http://localhost:28300]
import { chromium } from 'playwright-core';
import fs from 'node:fs';
const U = process.argv[2] || 'http://localhost:28300';
let failed = 0;
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? `  (${extra})` : ''}`); if (!ok) failed++; };

const auth = await (await fetch(`${U}/Users/AuthenticateByName`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: 'MediaBrowser Client="e2e", Device="cli", DeviceId="e2e", Version="1"' }, body: JSON.stringify({ Username: 'admin', Pw: 'test' }) })).json();
const H = { Authorization: `MediaBrowser Token="${auth.AccessToken}"`, 'Content-Type': 'application/json' };
const api = async (path, body, method) => { const r = await fetch(`${U}/MyLists/api/${path}`, { method: method || (body ? 'POST' : 'GET'), headers: H, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, data: await r.json().catch(() => null) }; };
const created = [];
const mk = async (b) => { const r = await api('lists', b); if (r.data?.id) created.push(r.data.id); return r; };
const movies = (await (await fetch(`${U}/Users/${auth.User.Id}/Items?IncludeItemTypes=Movie&Recursive=true&Fields=ProviderIds`, { headers: H })).json()).Items;
const byName = (n) => movies.find((m) => m.Name === n);

try {
  check('anonymous API is rejected', (await fetch(`${U}/MyLists/api/lists`)).status === 401);

  // rules: the role matters (producer is not director), names are case-insensitive
  check('director rule: Buster Keaton -> 2', (await mk({ name: 'e2e keaton', sourceType: 'rule', rule: { director: 'buster  keaton' } })).data.total === 2);
  check('director rule: producer-only person -> 0', (await mk({ name: 'e2e pommer', sourceType: 'rule', rule: { director: 'Erich Pommer' } })).data.total === 0);
  check('actor rule: producer-only person -> 0', (await mk({ name: 'e2e pommer a', sourceType: 'rule', rule: { actor: 'Erich Pommer' } })).data.total === 0);
  check('director + non-matching genre -> 0', (await mk({ name: 'e2e hawks', sourceType: 'rule', rule: { director: 'Howard Hawks', genre: 'Horror' } })).data.total === 0);
  check('empty rule matches nothing', (await mk({ name: 'e2e empty', sourceType: 'rule', rule: {} })).data.total === 0);

  // text import: messy assistant output, ids beat titles
  const messy = 'Here is your list:\n```\n1. **Metropolis** (1927)\n2. Nosferatu - 1922\n3. "The General", 1926\nBlade Runner 2049\n```';
  const prev = (await api('preview', { text: messy })).data;
  check('preview understands 4 lines, 3 in library', prev.length === 4 && prev.filter((r) => r.match).length === 3, `${prev.length}/${prev.filter((r) => r.match).length}`);
  const cald = byName('The Cabinet of Dr. Caligari');
  const idOnly = (await api('preview', { text: `Position,Const,Title,Year\n1,${cald.ProviderIds.Imdb},Completely Wrong Title,1920` })).data;
  check('IMDb id matches even with a wrong title', idOnly[0]?.match?.startsWith('The Cabinet of Dr. Caligari'));
  const csv = await mk({ name: 'e2e csv', sourceType: 'csv', defaultSort: 'source', text: messy });
  check('csv list created (4 entries, 3 available)', csv.data.total === 4 && csv.data.available === 3);
  check('letterboxd export with metadata header parses', (await api('preview', { text: 'Date,Name,Tags,URL,Description\n2024-01-01,My list,,u,\n\nPosition,Name,Year,URL,Description\n1,Metropolis,1927,u,\n' })).data.length === 1);

  // mdblist (needs internet)
  const mdb = await mk({ name: 'e2e top250', sourceType: 'mdblist', sourceUrl: 'https://mdblist.com/lists/snoak/imdb-top-250-movies' });
  check('MDBList IMDb Top 250 imports 250 entries', mdb.data?.total === 250, mdb.data?.error || `available ${mdb.data?.available}`);
  check('MDBList bad url -> clean error', (await api('lists', { name: 'x', sourceType: 'mdblist', sourceUrl: 'https://example.com/lists/a/b' })).status === 422);
  check('letterboxd blocked/missing -> clean error, no list left behind', ![200, 201].includes((await api('lists', { name: 'x', sourceType: 'letterboxd', sourceUrl: 'https://letterboxd.com/nobody/list/none/' })).status));

  // watched state, order, isolation
  const nos = byName('Nosferatu');
  await fetch(`${U}/Users/${auth.User.Id}/PlayedItems/${nos.Id}`, { method: 'POST', headers: H });
  let d = (await api(`lists/${csv.data.id}`)).data;
  check('watched state comes from Jellyfin', d.entries.find((e) => e.title === 'Nosferatu')?.played === true);
  await api(`lists/${csv.data.id}/order`, { keys: [...d.entries].reverse().map((e) => e.key) });
  d = (await api(`lists/${csv.data.id}`)).data;
  check('saved order is kept', d.entries.find((e) => e.title === 'Metropolis').manualPos === d.entries.length - 1);
  check('invalid default sort is ignored', ((await api(`lists/${csv.data.id}`, { defaultSort: 'nonsense' })), (await api(`lists/${csv.data.id}`)).data.list.defaultSort === 'source'));
  const manual = await mk({ name: 'e2e manual', sourceType: 'manual' });
  await api(`lists/${manual.data.id}/entries`, { itemId: byName('Metropolis').Id });
  await api(`lists/${manual.data.id}/entries`, { itemId: byName('Metropolis').Id });
  check('adding a movie twice keeps one entry', (await api(`lists/${manual.data.id}`)).data.entries.length === 1);

  // cover
  const png = fs.readFileSync(new URL('./test-cover.png', import.meta.url));
  const up = await fetch(`${U}/MyLists/api/lists/${manual.data.id}/cover`, { method: 'POST', headers: { Authorization: H.Authorization, 'Content-Type': 'image/png' }, body: png });
  const cover = (await up.json()).cover;
  check('cover upload accepted and served', up.status === 200 && (await fetch(`${U}/MyLists/cover/${cover}`)).headers.get('content-type') === 'image/png');
  const bad = await fetch(`${U}/MyLists/api/lists/${manual.data.id}/cover`, { method: 'POST', headers: { Authorization: H.Authorization, 'Content-Type': 'image/png' }, body: Buffer.from('<html>not an image</html>') });
  check('non-image upload rejected', bad.status === 400);

  // browser: sidebar entry + hard navigation, poster greyed, details link
  const b = await chromium.launch({ executablePath: process.env.CHROME });
  const p = await b.newPage({ viewport: { width: 1280, height: 900 } });
  await p.addInitScript((c) => localStorage.setItem('jellyfin_credentials', JSON.stringify(c)), { Servers: [{ Id: auth.ServerId, AccessToken: auth.AccessToken, UserId: auth.User.Id, ManualAddress: U, LocalAddress: U, DateLastAccessed: Date.now() }] });
  await p.goto(`${U}/web/#/home`); await p.waitForTimeout(5000);
  await p.click('button[aria-label="Open drawer"], .mainDrawerButton, [aria-label*="enu"]'); await p.waitForTimeout(600);
  await p.locator('.mylists-link a, a.mylists-link').first().click(); await p.waitForTimeout(2000);
  check('sidebar link opens /MyLists/', p.url() === `${U}/MyLists/`, p.url());
  await p.goto(`${U}/MyLists/#/list/${csv.data.id}`); await p.waitForSelector('.poster');
  const playedNow = (await api(`lists/${csv.data.id}`)).data.entries.filter((e) => e.inLibrary && e.played).length;
  check('watched posters are greyed (one per played movie)', playedNow >= 1 && (await p.locator('.poster.watched').count()) === playedNow, `${playedNow}`);
  await p.selectOption('#sort', 'title'); await p.locator('.poster').nth(2).dragTo(p.locator('.poster').nth(0));
  check('drag & drop offers "Save this order"', (await p.locator('#save').count()) === 1);
  await p.click('.poster >> nth=0'); await p.waitForTimeout(3500);
  check('poster opens the Jellyfin details page', /#\/details\?id=[0-9a-f]{32}&serverId=/.test(p.url()) && (await p.locator('.detailPageContent, .itemDetailPage').count()) > 0, p.url());
  await b.close();
} finally {
  for (const id of created) await api(`lists/${id}`, undefined, 'DELETE');
}
console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
