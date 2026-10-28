const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const DEFAULT_TEMPLATE = '{"title":"{title}","content":"{content}"}';
const FILE_NAME = 'webhooks.encrypted';

function store(directory, secure) {
  const file = path.join(directory, FILE_NAME);
  function read() {
    if (!fs.existsSync(file)) return [];
    if (!secure.isEncryptionAvailable()) throw new Error('Windows 加密不可用，无法读取 Webhook；请检查系统账户。');
    try {
      const data = JSON.parse(secure.decryptString(fs.readFileSync(file)));
      if (!Array.isArray(data)) throw new Error('invalid');
      return data.map(validate);
    } catch (_) { throw new Error('Webhook 配置无法解密或无效，请在原 Windows 账户中恢复或重新配置。'); }
  }
  function write(items) {
    if (!secure.isEncryptionAvailable()) throw new Error('Windows 加密不可用，无法保存 Webhook。');
    const encrypted = secure.encryptString(JSON.stringify(items));
    fs.mkdirSync(directory, { recursive: true });
    const temporary = `${file}.${crypto.randomUUID()}.tmp`;
    try { fs.writeFileSync(temporary, encrypted, { flag: 'wx' }); fs.renameSync(temporary, file); }
    finally { if (fs.existsSync(temporary)) fs.unlinkSync(temporary); }
  }
  return {
    list: () => read().map(({ id, name, enabled, method }) => ({ id, name, enabled, method })),
    get: (id) => { const item = read().find(value => value.id === id); if (!item) throw new Error('Webhook 不存在。'); return item; },
    save: (input) => { const items = read(); const id = input.id || crypto.randomUUID(); const index = items.findIndex(value => value.id === id); if (input.id && index < 0) throw new Error('Webhook 不存在。'); const item = validate({ ...input, id }); if (index < 0) items.push(item); else items[index] = item; write(items); return { id: item.id, name: item.name, enabled: item.enabled, method: item.method }; },
    remove: (id) => { const items = read(); const next = items.filter(item => item.id !== id); if (items.length === next.length) throw new Error('Webhook 不存在。'); write(next); },
    resetUnreadable: () => { if (!fs.existsSync(file)) return; try { read(); } catch (error) { if (!secure.isEncryptionAvailable()) throw error; const backup = `${file}.${crypto.randomUUID()}.unreadable`; fs.copyFileSync(file, backup, fs.constants.COPYFILE_EXCL); write([]); return; } throw new Error('Webhook 配置可正常读取，无需重新配置。'); },
  };
}
function validate(input) {
  if (!input || typeof input.id !== 'string' || typeof input.name !== 'string' || !input.name.trim() || input.name.length > 100) throw new Error('Webhook 名称无效。');
  let url; try { url = new URL(input.url); } catch (_) { throw new Error('Webhook 地址无效。'); }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password) throw new Error('Webhook 地址仅支持 HTTP 或 HTTPS，且不能包含用户名或密码。');
  if (!['GET', 'POST'].includes(input.method) || typeof input.enabled !== 'boolean') throw new Error('Webhook 方法或启用状态无效。');
  if (typeof input.template !== 'string' || input.template.length > 32768 || !input.headers || typeof input.headers !== 'object' || Array.isArray(input.headers)) throw new Error('Webhook 模板或请求头无效。');
  const headers = {}; for (const [key, value] of Object.entries(input.headers)) { if (!/^[!#$%&'*+.^_`|~\w-]+$/.test(key) || typeof value !== 'string' || /[\r\n]/.test(value)) throw new Error('Webhook 请求头无效。'); headers[key] = value; }
  return { id: input.id, name: input.name.trim(), url: url.href, method: input.method, enabled: input.enabled, template: input.template || DEFAULT_TEMPLATE, headers };
}
function format(template, message, now = new Date()) {
  const date = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  const time = [now.getHours(), now.getMinutes(), now.getSeconds()].map(v => String(v).padStart(2, '0')).join(':');
  const vars = { title: message.title, content: message.body, date, time, datetime: `${date} ${time}` };
  const replace = value => value.replace(/\{(title|content|datetime|date|time)\}/g, (_, key) => vars[key]);
  try { const walk = value => typeof value === 'string' ? replace(value) : Array.isArray(value) ? value.map(walk) : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).map(([k, v]) => [k, walk(v)])) : value; return walk(JSON.parse(template)); }
  catch (_) { return replace(template); }
}
async function send(item, message, timeout = 10000) {
  const data = format(item.template, message); const url = new URL(item.url); const headers = { 'Content-Type': 'application/json', ...item.headers };
  let body;
  if (item.method === 'GET') {
    if (data && typeof data === 'object' && !Array.isArray(data)) for (const [key, value] of Object.entries(data)) url.searchParams.append(key, typeof value === 'object' ? JSON.stringify(value) : String(value));
    else url.searchParams.append('message', typeof data === 'string' ? data : JSON.stringify(data));
  } else body = typeof data === 'string' ? data : JSON.stringify(data);
  let response; try { response = await fetch(url, { method: item.method, headers, body, redirect: 'error', signal: AbortSignal.timeout(timeout) }); } catch (_) { throw new Error('Webhook 请求失败或超时，请检查网络与地址。'); }
  if (!response.ok) throw new Error(`Webhook 请求失败（HTTP ${response.status}）。`);
  return { sent: true };
}
module.exports = { DEFAULT_TEMPLATE, FILE_NAME, store, validate, format, send };
