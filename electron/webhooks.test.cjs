const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { store, send, FILE_NAME, DEFAULT_TEMPLATE } = require('./webhooks.cjs');
const { createArchive } = require('./settings.cjs');

test('encrypted multi-hook CRUD, unavailable encryption, and archive boundaries', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-hooks-'));
  const secure = { isEncryptionAvailable: () => true, encryptString: text => Buffer.from(text).reverse(), decryptString: data => Buffer.from(data).reverse().toString() };
  try {
    const hooks = store(dir, secure); const item = { name: 'one', url: 'http://127.0.0.1:1234/secret', method: 'POST', enabled: true, template: DEFAULT_TEMPLATE, headers: { Authorization: 'secret-token' } };
    const first = hooks.save(item); const second = hooks.save({ ...item, name: 'two' });
    assert.equal(hooks.list().length, 2); assert.equal(hooks.get(first.id).headers.Authorization, 'secret-token');
    assert.equal(fs.readFileSync(path.join(dir, FILE_NAME)).includes(Buffer.from('secret-token')), false);
    hooks.save({ ...hooks.get(first.id), enabled: false }); assert.equal(hooks.get(first.id).enabled, false);
    hooks.remove(second.id); assert.equal(hooks.list().length, 1);
    assert.throws(() => store(dir, { ...secure, isEncryptionAvailable: () => false }).list(), /加密不可用/);
    fs.writeFileSync(path.join(dir, 'settings.json'), '{"autoStart":false}');
    for (const kind of ['backup', 'logs', 'diagnostic']) { const dest = path.join(dir, `${kind}.zip`); createArchive(dir, dest, { kind, presetId: 'maa' }); const zip = fs.readFileSync(dest); assert.equal(zip.includes(Buffer.from(FILE_NAME)), false); assert.equal(zip.includes(Buffer.from('secret-token')), false); assert.equal(zip.includes(Buffer.from('/secret')), false); }
    assert.throws(() => hooks.resetUnreadable(), /无需重新配置/);
    fs.writeFileSync(path.join(dir, FILE_NAME), 'unreadable'); assert.throws(() => hooks.list(), /无法解密/); hooks.resetUnreadable(); assert.deepEqual(hooks.list(), []); assert.equal(fs.readdirSync(dir).filter(name => name.endsWith('.unreadable')).length, 1);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('GET and POST templates and headers use a local controlled server', async () => {
  const requests = []; const server = http.createServer((req, res) => { let body = ''; req.on('data', chunk => { body += chunk; }); req.on('end', () => { requests.push({ method: req.method, url: req.url, header: req.headers['x-fixture'], body }); if (req.url.includes('timeout')) { setTimeout(() => res.end('late'), 100); return; } res.writeHead(req.url.includes('failure') ? 503 : 200); res.end('ok'); }); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const url = `http://127.0.0.1:${server.address().port}/notify`;
    await send({ url, method: 'POST', template: DEFAULT_TEMPLATE, headers: { 'x-fixture': 'local' } }, { title: '标题"', body: '正文\n内容' });
    assert.deepEqual(JSON.parse(requests[0].body), { title: '标题"', content: '正文\n内容' }); assert.equal(requests[0].header, 'local');
    await send({ url, method: 'GET', template: '{"title":"{title}","content":"{content}"}', headers: {} }, { title: '标题', body: '正文' });
    const query = new URL(requests[1].url, url); assert.equal(query.searchParams.get('title'), '标题'); assert.equal(query.searchParams.get('content'), '正文');
    await assert.rejects(send({ url: `${url}/failure`, method: 'POST', template: 'test', headers: {} }, { title: '', body: '' }), /HTTP 503/);
    await assert.rejects(send({ url: `${url}/timeout`, method: 'POST', template: 'test', headers: {} }, { title: '', body: '' }, 10), /失败或超时/);
  } finally { await new Promise(resolve => server.close(resolve)); }
});
