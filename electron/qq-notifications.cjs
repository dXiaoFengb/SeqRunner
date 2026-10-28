const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const QRCode = require('qrcode');

const FILE = 'qq-notification.encrypted';
const HOSTS = { portal: 'https://q.qq.com', api: 'https://api.sgroup.qq.com', token: 'https://bots.qq.com/app/getAppAccessToken' };

function createQQNotifications({ directory, secure, request = fetch, WebSocketClass = globalThis.WebSocket, now = () => Date.now(), hosts = HOSTS, reconnectDelay = 5000 }) {
  const file = path.join(directory, FILE);
  let credentials = null, enabled = false, token = null, tokenUntil = 0, session = null, epoch = 0, socket = null, heartbeat = null, reconnect = null, online = false, failed = false, sequence = null, msgSeq = 0, stopped = false;
  function persist(value) {
    if (!secure.isEncryptionAvailable()) throw new Error('Windows 加密不可用，无法保存 QQ 绑定。');
    const buffer = secure.encryptString(JSON.stringify(value)); fs.mkdirSync(directory, { recursive: true });
    const temp = `${file}.${crypto.randomUUID()}.tmp`;
    try { fs.writeFileSync(temp, buffer, { flag: 'wx' }); fs.renameSync(temp, file); } finally { if (fs.existsSync(temp)) fs.unlinkSync(temp); }
  }
  try { if (fs.existsSync(file)) { if (!secure.isEncryptionAvailable()) throw new Error('加密不可用'); const data = JSON.parse(secure.decryptString(fs.readFileSync(file))); credentials = data.credentials || null; enabled = data.enabled === true; } } catch (_) { throw new Error('QQ 绑定无法解密，请在原 Windows 账户中恢复。'); }
  async function json(url, options = {}) {
    let response; try { response = await request(url, { ...options, redirect: 'error', signal: AbortSignal.timeout(15000), headers: { Accept: 'application/json', 'Content-Type': 'application/json', ...options.headers } }); } catch (_) { throw new Error('QQ 官方服务连接失败或超时。'); }
    if (!response.ok) { const error = new Error(`QQ 官方服务请求失败（HTTP ${response.status}）。`); error.status = response.status; throw error; }
    let payload; try { payload = await response.json(); } catch (_) { throw new Error('QQ 官方服务返回无效数据。'); }
    if (!payload || typeof payload !== 'object' || (payload.code != null && Number(payload.code) !== 0)) throw new Error('QQ 官方服务拒绝请求。');
    return payload;
  }
  function disconnect() { online = false; if (heartbeat) clearInterval(heartbeat); if (reconnect) clearTimeout(reconnect); heartbeat = reconnect = null; const old = socket; socket = null; if (old) old.close(); }
  async function accessToken() {
    if (!credentials) throw new Error('请先绑定 QQ 官方机器人。');
    if (token && tokenUntil > now() + 60000) return token;
    const data = await json(hosts.token, { method:'POST', body: JSON.stringify({ appId:credentials.appId, clientSecret:credentials.secret }) });
    if (typeof data.access_token !== 'string' || !data.access_token) throw new Error('QQ 令牌响应无效。');
    token = data.access_token; tokenUntil = now() + Math.max(60, Number(data.expires_in) || 7200) * 1000; return token;
  }
  async function connect(generation = epoch) {
    if (!credentials || !enabled || stopped || generation !== epoch) return;
    try {
      const auth = await accessToken(); const data = await json(`${hosts.api}/gateway`, { headers:{ Authorization:`QQBot ${auth}`, 'X-Union-Appid':credentials.appId } });
      const gateway = new URL(data.url); if (gateway.protocol !== 'wss:' || !(gateway.hostname === 'qq.com' || gateway.hostname.endsWith('.qq.com'))) throw new Error('QQ 网关地址无效。');
      if (generation !== epoch || stopped) return;
      const ws = new WebSocketClass(gateway.href); socket = ws;
      ws.onmessage = event => { if (generation !== epoch) return; try { const msg = JSON.parse(String(event.data)); if (Number.isInteger(msg.s)) sequence = msg.s; if (msg.op === 10) { const ms = Number(msg.d?.heartbeat_interval); if (!Number.isFinite(ms) || ms < 1000) { ws.close(); return; } ws.send(JSON.stringify({ op:2, d:{ token:`QQBot ${auth}`, intents:1 << 25, shard:[0,1], properties:{ '$os':'windows', '$browser':'sequential-runner', '$device':'sequential-runner' } } })); heartbeat = setInterval(() => { if (socket === ws && ws.readyState === 1) ws.send(JSON.stringify({ op:1, d:sequence })); }, Math.floor(ms * 0.8)); } else if (msg.op === 0 && msg.t === 'READY') { online = true; failed = false; } else if (msg.op === 7 || msg.op === 9) { token = null; ws.close(); } } catch (_) { ws.close(); } };
      ws.onerror = () => { ws.close(); }; ws.onclose = () => { if (socket !== ws) return; socket = null; online = false; failed = true; if (heartbeat) clearInterval(heartbeat); heartbeat = null; if (!stopped && enabled && generation === epoch) reconnect = setTimeout(() => void connect(generation), reconnectDelay); };
    } catch (_) { online = false; failed = true; if (!stopped && enabled && generation === epoch) reconnect = setTimeout(() => void connect(generation), reconnectDelay); }
  }
  return {
    status: () => ({ bound: !!credentials, enabled, state: session ? 'binding' : !credentials ? 'unbound' : !enabled ? 'disabled' : online ? 'connected' : failed ? 'failed' : 'connecting' }),
    start: async () => { const generation = ++epoch; disconnect(); session = null; try { const key = crypto.randomBytes(32); const data = await json(`${hosts.portal}/lite/create_bind_task`, { method:'POST', body:JSON.stringify({ key:key.toString('base64') }) }); if (generation !== epoch) throw new Error('绑定请求已取消。'); const task = String(data.data?.task_id || ''); if (!task) throw new Error('QQ 扫码任务无效。'); const id = crypto.randomUUID(); session = { id, task, key, expires:now()+300000, generation }; const qr = new URL('/qqbot/openclaw/connect.html', hosts.portal); qr.searchParams.set('task_id', task); qr.searchParams.set('_wv','2'); const image = await QRCode.toDataURL(qr.href); if (generation !== epoch) throw new Error('绑定请求已取消。'); return { id, image }; } catch (error) { if (generation === epoch) { session = null; if (credentials && enabled) void connect(epoch); } throw error; } },
    poll: async id => { const current = session; if (!current || current.id !== id) throw new Error('扫码会话已取消。'); if (now() >= current.expires) { session = null; if (credentials && enabled) void connect(epoch); return { state:'expired' }; } const response = await json(`${hosts.portal}/lite/poll_bind_result`, { method:'POST', body:JSON.stringify({ task_id:current.task }) }); if (session !== current || current.generation !== epoch) throw new Error('扫码会话已取消。'); const data = response.data || {}; if (Number(data.status) === 3) { session = null; if (credentials && enabled) void connect(epoch); return { state:'expired' }; } if (Number(data.status) !== 2) return { state:Number(data.status) === 1 ? 'scanned' : 'waiting' }; const encrypted = Buffer.from(String(data.bot_encrypt_secret || ''), 'base64'); if (encrypted.length <= 28 || !data.bot_appid || !data.user_openid) throw new Error('QQ 扫码结果不完整。'); const decipher = crypto.createDecipheriv('aes-256-gcm', current.key, encrypted.subarray(0,12)); decipher.setAuthTag(encrypted.subarray(-16)); let secret; try { secret = Buffer.concat([decipher.update(encrypted.subarray(12,-16)), decipher.final()]).toString('utf8'); } catch (_) { throw new Error('QQ 扫码凭据无法解密。'); } if (!secret) throw new Error('QQ 扫码凭据无效。'); const next = { appId:String(data.bot_appid), secret, target:String(data.user_openid) }; persist({ credentials:next, enabled:true }); credentials = next; enabled = true; token = null; session = null; void connect(epoch); return { state:'connecting' }; },
    cancel: () => { ++epoch; session = null; disconnect(); failed = false; if (credentials && enabled) void connect(epoch); },
    setEnabled: value => { if (typeof value !== 'boolean' || (!credentials && value)) throw new Error('请先绑定 QQ 官方机器人。'); persist({ credentials, enabled:value }); enabled = value; ++epoch; disconnect(); if (enabled) void connect(epoch); },
    unbind: () => { persist({ credentials:null, enabled:false }); ++epoch; session = null; credentials = null; enabled = false; token = null; disconnect(); },
    send: async message => { if (!credentials || !enabled) throw new Error('QQ 官方机器人未启用或未绑定。'); const generation = epoch; const target = credentials.target; const appId = credentials.appId; msgSeq = (msgSeq % 4294967295) + 1; const body = JSON.stringify({ msg_type:0, msg_seq:msgSeq, content:`${message.title}\n${message.body}`.slice(0,3800) }); for (let attempt = 0; attempt < 2; attempt++) { const auth = await accessToken(); if (generation !== epoch || !enabled) throw new Error('QQ 绑定状态已更改。'); try { await json(`${hosts.api}/v2/users/${encodeURIComponent(target)}/messages`, { method:'POST', headers:{ Authorization:`QQBot ${auth}`, 'X-Union-Appid':appId }, body }); return { sent:true }; } catch (error) { if (attempt || error.status !== 401) throw error; token = null; } } },
    startGateway: () => { stopped = false; if (credentials && enabled) void connect(epoch); },
    stop: () => { stopped = true; ++epoch; session = null; disconnect(); },
  };
}
module.exports = { createQQNotifications, FILE };
