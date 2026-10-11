import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const html = await readFile(new URL('../public/admin.html', import.meta.url), 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
function page(fetch) {
  const elements = new Map();
  const copied = [];
  function element() { return { value: '', hidden: false, disabled: false, textContent: '', children: [], select() {}, append(...items) { this.children.push(...items); }, replaceChildren(...items) { this.children = items; } }; }
  const document = { getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); }, createElement: element };
  runInNewContext(script, { document, fetch, AbortSignal, navigator: { clipboard: { async writeText(value) { copied.push(value); } } } });
  const el = id => document.getElementById(id);
  el('management').hidden = true;
  return { el, copied, async login(value) { el('key').value = value; await el('connect').onsubmit({ preventDefault() {} }); } };
}

test('admin page rejects a client card with a useful explanation before sending it', async () => {
  let requests = 0;
  const ui = page(async () => { requests++; });
  await ui.login('PUREISP-00000000-00000000-00000000-00000000');
  assert.equal(requests, 0);
  assert.match(ui.el('notice').textContent, /客户端卡密.*ADMIN_KEY/);
  assert.equal(ui.el('management').hidden, true);
  assert.equal(ui.el('login').disabled, false);
});

test('admin login accepts a complete env line and renders the card list as text', async () => {
  const key = 'a'.repeat(64);
  let sent;
  const ui = page(async (_path, options) => {
    sent = options.headers['X-Admin-Key'];
    return { status: 200, ok: true, async json() { return { cards: [{ id: '1'.repeat(24), label: '<script>test</script>', hint: '…12345678', status: 'active', expiresAt: '2027-10-11T00:00:00Z' }] }; } };
  });
  await ui.login(' ADMIN_KEY="' + key + '" ');
  assert.equal(sent, key);
  assert.equal(ui.el('key').value, '');
  assert.equal(ui.el('management').hidden, false);
  assert.equal(ui.el('connect').hidden, true);
  assert.equal(ui.el('cards').children[0].children[0].children[0].children[0].textContent, '<script>test</script>');
  assert.equal(ui.el('cards').children[0].children[3].children[0].value, '2027-10-11T08:00:00.000');
  ui.el('exit').onclick();
  assert.equal(ui.el('management').hidden, true);
  assert.equal(ui.el('cards').children.length, 0);
});

test('Beijing expiry edits preserve the UTC instant including seconds and milliseconds', async () => {
  const expiry = '2027-10-11T02:08:52.444Z';
  const card = { id: '1'.repeat(24), label: 'test', hint: 'test', status: 'active', expiresAt: expiry };
  const submitted = [];
  const ui = page(async (_path, options) => {
    if (options.body) submitted.push(JSON.parse(options.body));
    return { status: 200, ok: true, async json() { return options.method === 'GET' ? { cards: [card] } : { ...card, licenseKey: 'test-card' }; } };
  });
  await ui.login('a'.repeat(64));
  const row = ui.el('cards').children[0];
  assert.equal(row.children[3].children[0].value, '2027-10-11T10:08:52.444');
  await row.children[4].children[0].children[1].onclick();
  assert.equal(submitted[0].expiresAt, expiry, 'saving the displayed time does not shift or truncate the expiry');
  ui.el('expiry').value = '2027-10-11T10:08:52.444';
  await ui.el('mint').onsubmit({ preventDefault() {} });
  assert.equal(submitted[1].expiresAt, expiry, 'new card dates are interpreted as Beijing time');
});

test('wrong admin key and unavailable service produce distinct errors', async () => {
  const badKey = page(async () => ({ status: 401 }));
  await badKey.login('b'.repeat(64));
  assert.match(badKey.el('notice').textContent, /管理员密钥不正确/);
  assert.equal(badKey.el('management').hidden, true);
  const offline = page(async () => { throw Error('offline'); });
  await offline.login('c'.repeat(64));
  assert.match(offline.el('notice').textContent, /无法连接授权服务/);
  assert.equal(offline.el('login').disabled, false);
});

test('complete cards display on login with copying in the operations column; legacy cards are marked', async () => {
  const complete = 'PUREISP-11111111-22222222-33333333-12345678';
  const card = { id: '1'.repeat(24), label: 'customer', hint: '…12345678', status: 'active', expiresAt: '2027-10-11T00:00:00Z', keyStored: true, licenseKey: complete };
  const paths = [];
  const ui = page(async (path, options) => {
    assert.equal(options.headers['X-Admin-Key'], 'a'.repeat(64));paths.push(path);
    return { status: 200, ok: true, async json() { return { cards: [card, { ...card, id: '2'.repeat(24), keyStored: false, licenseKey: undefined }] }; } };
  });
  await ui.login('a'.repeat(64));
  assert.deepEqual(paths, ['/v1/admin/cards?includeKeys=true'], 'all displayed cards are fetched in one administrator request');
  const rows=ui.el('cards').children;
  const buttons=rows.map(row=>row.children[4].children[0].children[0]);
  assert.equal(rows[0].children[0].children[0].children[0].textContent, 'customer');
  assert.equal(rows[0].children[1].children[0].value, complete, 'complete card is visible without a reveal click');
  assert.equal(buttons[0].textContent, '复制卡密');assert.equal(buttons[0].disabled, false);
  assert.equal(buttons[1].disabled, true);assert.match(rows[1].children[1].children[0].value, /旧卡/);
  assert.equal(rows[0].children[0].children[0].children.length, 1, 'account column contains only the name');
  assert.equal(rows[0].children[4].children[0].children.length, 4, 'copying and license controls share the operations column');
  await buttons[0].onclick();assert.deepEqual(ui.copied, [complete]);
  ui.el('exit').onclick();assert.equal(ui.el('cards').children.length, 0);assert.equal(ui.el('management').hidden, true);
});

test('a pending list refresh cannot put complete cards back on the page after logout', async () => {
  let finish, requests=0;
  const ui=page(async()=>{
    if(++requests>1)return await new Promise(resolve=>{finish=resolve;});
    return {status:200,ok:true,async json(){return {cards:[]};}};
  });
  await ui.login('a'.repeat(64));
  const pending=ui.el('refresh').onclick();ui.el('exit').onclick();
  finish({status:200,ok:true,async json(){return {cards:[{id:'1'.repeat(24),licenseKey:'secret-card'}]};}});
  await pending;
  assert.equal(ui.el('cards').children.length, 0);assert.equal(ui.el('management').hidden, true);
});

test('search finds account names, full cards, suffixes and IDs without additional requests', async () => {
  const complete='PUREISP-11111111-22222222-33333333-ABCDEF12';
  const cards=[{id:'1'.repeat(24),label:'用户583921',hint:'…ABCDEF12',licenseKey:complete,status:'active',createdAt:'2026-01-01T00:00:00Z',expiresAt:'2027-10-11T00:00:00Z'},
    {id:'2'.repeat(24),label:'管理员',hint:'…99999999',status:'disabled',createdAt:'2027-12-01T00:00:00Z',expiresAt:'2027-10-11T00:00:00Z'},
    {id:'3'.repeat(24),label:'新用户',hint:'…22222222',licenseKey:'PUREISP-AAAAAAAA-BBBBBBBB-CCCCCCCC-22222222',status:'active',createdAt:'2027-01-01T00:00:00Z',expiresAt:'2027-10-11T00:00:00Z'}];
  let requests=0;
  const ui=page(async()=>{requests++;return {status:200,ok:true,async json(){return {cards:[...cards].reverse()};}};});
  await ui.login('a'.repeat(64));
  const labels=()=>ui.el('cards').children.map(row=>row.children[0].children[0].children[0].textContent);
  assert.deepEqual(labels(),['新用户','用户583921','管理员'],'newest accounts come first and disabled cards always come last');
  for(const query of ['用户583921',complete.toLowerCase(),'abcdef12','1'.repeat(24),'用户 abcdef12']){
    ui.el('search').value=query;ui.el('search').oninput();
    assert.equal(ui.el('cards').children.length,1);
    assert.equal(ui.el('cards').children[0].children[0].children[0].children[0].textContent,'用户583921');
  }
  ui.el('search').value='不存在';ui.el('search').oninput();
  assert.equal(ui.el('cards').children.length,0);assert.equal(ui.el('empty').hidden,false);
  assert.equal(ui.el('resultCount').textContent,'显示 0 / 共 3 条');
  ui.el('clearSearch').onclick();assert.equal(ui.el('cards').children.length,3);
  assert.deepEqual(labels(),['新用户','用户583921','管理员'],'clearing a search preserves new-first, disabled-last ordering');
  ui.el('statusFilter').value='disabled';ui.el('statusFilter').onchange();
  assert.equal(ui.el('cards').children.length,1);
  assert.equal(ui.el('cards').children[0].children[0].children[0].children[0].textContent,'管理员');
  assert.equal(requests,1,'typing and status filtering never contact the service or database');
});

test('expiring filter renews the selected account and preserves filters after reloading', async () => {
  const expiresAt=new Date(Date.now()+2*86400000).toISOString();
  const cards=[{id:'1'.repeat(24),label:'近期客户',status:'active',expiresAt},
    {id:'2'.repeat(24),label:'长期客户',status:'active',expiresAt:new Date(Date.now()+60*86400000).toISOString()},
    {id:'3'.repeat(24),label:'到期客户',status:'expired',expiresAt:new Date(Date.now()-86400000).toISOString()}];
  let saved;
  const ui=page(async(path,options)=>{
    if(options.method==='PUT'){
      saved={path,...JSON.parse(options.body)};
      cards[0].expiresAt=saved.expiresAt;
    }
    return {status:200,ok:true,async json(){return options.method==='GET'?{cards}:{};}};
  });
  await ui.login('a'.repeat(64));
  ui.el('search').value='客户';ui.el('statusFilter').value='expiring';ui.el('statusFilter').onchange();
  assert.equal(ui.el('cards').children.length,1);
  const row=ui.el('cards').children[0];
  await row.children[4].children[0].children[2].onclick();
  assert.equal(saved.path,'/v1/admin/cards/'+cards[0].id+'/license');
  assert.equal(Date.parse(saved.expiresAt),Date.parse(expiresAt)+30*86400000);
  assert.equal(saved.enabled,true);
  assert.equal(ui.el('search').value,'客户');assert.equal(ui.el('statusFilter').value,'expiring');
  assert.equal(ui.el('cards').children.length,0,'renewed account no longer matches the expiring filter');
  ui.el('statusFilter').value='expired';ui.el('statusFilter').onchange();
  assert.equal(ui.el('cards').children.length,1);
  assert.equal(ui.el('cards').children[0].children[0].children[0].children[0].textContent,'到期客户');
  ui.el('exit').onclick();ui.el('search').oninput();assert.equal(ui.el('cards').children.length,0);
});
