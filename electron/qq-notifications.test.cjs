const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createQQNotifications, FILE } = require('./qq-notifications.cjs');

test('QQ binding, encryption, token, gateway and local fixture send', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-qq-')); let key, ticks = 0, sends = 0; const requests = [];
  const secure = { isEncryptionAvailable:() => true, encryptString:value => Buffer.from(value).reverse(), decryptString:value => Buffer.from(value).reverse().toString() };
  const response = data => ({ ok:true, json:async () => data });
  const request = async (url, options) => { requests.push({url,options}); const body = options.body ? JSON.parse(options.body) : {};
    if (url.endsWith('/create_bind_task')) { key = Buffer.from(body.key, 'base64'); return response({code:0,data:{task_id:'fixture-task'}}); }
    if (url.endsWith('/poll_bind_result')) { if (!ticks++) return response({code:0,data:{status:1}}); const iv = crypto.randomBytes(12), cipher = crypto.createCipheriv('aes-256-gcm', key, iv); const encrypted = Buffer.concat([iv,cipher.update('fixture-secret'),cipher.final(),cipher.getAuthTag()]).toString('base64'); return response({code:0,data:{status:2,bot_appid:'123',bot_encrypt_secret:encrypted,user_openid:'fixture-target'}}); }
    if (url.endsWith('getAppAccessToken')) return response({access_token:'fixture-token',expires_in:7200});
    if (url.endsWith('/gateway')) return response({url:'wss://gateway.qq.com/'});
    if (url.includes('/messages')) { if (!sends++) return {ok:false,status:401}; return response({id:'fixture-message'}); }
    throw new Error('unexpected request');
  };
  class FakeSocket { constructor() { this.readyState=1; setImmediate(() => this.onmessage({data:JSON.stringify({op:10,d:{heartbeat_interval:10000}})})); } send(value) { if (JSON.parse(value).op === 2) setImmediate(() => this.onmessage({data:JSON.stringify({op:0,t:'READY'})})); } close() { this.readyState=3; this.onclose?.(); } }
  const qq = createQQNotifications({directory:dir,secure,request,WebSocketClass:FakeSocket});
  try {
    assert.equal(qq.status().state,'unbound'); const session = await qq.start(); assert.match(session.image,/^data:image\/png;base64,/);
    assert.equal((await qq.poll(session.id)).state,'scanned'); assert.equal((await qq.poll(session.id)).state,'connecting');
    await new Promise(resolve => setTimeout(resolve, 30)); assert.equal(qq.status().state,'connected');
    assert.equal(fs.readFileSync(path.join(dir,FILE)).includes(Buffer.from('fixture-secret')),false);
    await qq.send({title:'测试',body:'完成'}); assert.equal(sends,2); assert.equal(requests.filter(item => item.url.endsWith('getAppAccessToken')).length,2); assert.ok(requests.some(item => item.url.includes('/users/fixture-target/messages')));
    const { collectFiles } = require('./settings.cjs');
    for (const kind of ['backup','logs','diagnostic']) assert.equal(collectFiles(dir,kind).some(item => item.name === FILE),false);
    qq.setEnabled(false); assert.equal(qq.status().state,'disabled'); qq.unbind(); assert.equal(qq.status().state,'unbound');
    const pending = await qq.start(); qq.cancel(); await assert.rejects(qq.poll(pending.id),/取消/);
  } finally { qq.stop(); fs.rmSync(dir,{recursive:true,force:true}); }
});

test('QQ binding expiration and disabled encryption do not persist credentials', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-qq-')); let clock = 0;
  const qq = createQQNotifications({ directory:dir, secure:{isEncryptionAvailable:()=>false}, now:()=>clock, request:async () => ({ok:true,json:async()=>({code:0,data:{task_id:'fixture'}})}) });
  try { const session = await qq.start(); clock = 300001; assert.equal((await qq.poll(session.id)).state,'expired'); assert.throws(() => qq.unbind(),/加密不可用/); assert.equal(fs.existsSync(path.join(dir,FILE)),false); }
  finally { qq.stop(); fs.rmSync(dir,{recursive:true,force:true}); }
});

test('gateway disconnect retries and send failures remain visible', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'runner-qq-reconnect-'));
  const secure = { isEncryptionAvailable:() => true, decryptString:value => value.toString(), encryptString:value => Buffer.from(value) };
  fs.writeFileSync(path.join(dir,FILE), JSON.stringify({credentials:{appId:'fixture',secret:'fixture',target:'fixture'},enabled:true}));
  const sockets = [];
  class FakeSocket { constructor() { this.readyState=1; sockets.push(this); setImmediate(() => this.onmessage({data:JSON.stringify({op:0,t:'READY'})})); } close() { this.readyState=3; this.onclose?.(); } }
  const request = async url => url.endsWith('getAppAccessToken') ? {ok:true,json:async()=>({access_token:'fixture',expires_in:7200})} : url.endsWith('/gateway') ? {ok:true,json:async()=>({url:'wss://gateway.qq.com/'})} : {ok:false,status:503};
  const qq = createQQNotifications({directory:dir,secure,request,WebSocketClass:FakeSocket,reconnectDelay:10});
  try { qq.startGateway(); await new Promise(resolve => setTimeout(resolve,25)); assert.equal(qq.status().state,'connected'); sockets[0].close(); assert.equal(qq.status().state,'failed'); await new Promise(resolve => setTimeout(resolve,40)); assert.equal(sockets.length,2); assert.equal(qq.status().state,'connected'); await assert.rejects(qq.send({title:'fixture',body:'fixture'}),/HTTP 503/); }
  finally { qq.stop(); fs.rmSync(dir,{recursive:true,force:true}); }
});
