'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const REQUIRED = ['脚本配置', '顺序执行', '计划调度', '日志与历史', '进程收尾', '数据迁移', '设置', '通知', '产品文件'];
function hashFile(file) { return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex'); }
function readVersion(base) {
  const match = fs.readFileSync(path.join(base, 'VERSION.md'), 'utf8').match(/^\s*- 开发提交版本：\s*(v\d+\.\d+\.\d+)\s*$/m);
  if (!match) throw Error('无法读取开发版本'); return match[1];
}
function paths(base, version, attempt) {
  if (!/^v\d+\.\d+\.\d+$/.test(version) || !/^[A-Za-z0-9_-]{1,64}$/.test(attempt || '')) throw Error('版本或尝试标识无效');
  return { work: path.join(base, '产物', '工作', version, attempt), final: path.join(base, '产物', '验证', `${version}-${attempt}`), version, attempt };
}
function assertAbsent(target) { if (fs.existsSync(target)) throw Error('目标已存在，禁止覆盖：' + target); }
function assertSafeAncestors(base, target) {
  const rel = path.relative(path.resolve(base), path.resolve(target));
  if (!rel || rel.startsWith('..' + path.sep) || path.isAbsolute(rel)) throw Error('目标不在项目目录内');
  let current = path.resolve(base);
  for (const part of rel.split(path.sep)) {
    current = path.join(current, part);
    try { if (fs.lstatSync(current).isSymbolicLink()) throw Error('目标路径包含链接：' + current); }
    catch (e) { if (e.code !== 'ENOENT') throw e; }
  }
}
function findTrustedElectronArchive(cacheRoot, name, checksum) {
  if (!cacheRoot || !fs.existsSync(cacheRoot) || !/^[a-f0-9]{64}$/i.test(checksum || '')) throw Error('Electron 缓存或校验值缺失，不会下载');
  const found=[];
  function search(dir) { for (const item of fs.readdirSync(dir,{withFileTypes:true})) { if(item.isSymbolicLink()) continue; const file=path.join(dir,item.name); if(item.isDirectory()) search(file); else if(item.isFile() && item.name===name) found.push(file); } }
  search(cacheRoot);
  if(found.length!==1) throw Error('Electron 本地缓存缺失或不唯一');
  if(hashFile(found[0]).toLowerCase()!==checksum.toLowerCase()) throw Error('Electron 缓存 SHA-256 不匹配');
  return found[0];
}
function manifestFiles(dir) {
  const list = [];
  function visit(current, relative = '') {
    for (const item of fs.readdirSync(current, { withFileTypes: true })) {
      const rel = relative ? `${relative}/${item.name}` : item.name;
      if (rel === '文件清单.json') continue;
      if (item.isSymbolicLink() || (!item.isDirectory() && !item.isFile())) throw Error('产品包含链接或不支持的文件：' + rel);
      if ((!relative && ['userdata','data'].includes(item.name.toLowerCase())) || /^\.env(?:\.|$)/i.test(item.name) || /\.(?:key|pem)$/i.test(item.name) || /^(?:webhooks|qq-notification)\.encrypted$/i.test(item.name)) throw Error('产品包含用户数据或凭证：' + rel);
      const full = path.join(current, item.name);
      if (item.isDirectory()) visit(full, rel); else { const s = fs.statSync(full); list.push({ path: rel, size: s.size, sha256: hashFile(full) }); }
    }
  }
  visit(dir); return list.sort((a,b) => a.path.localeCompare(b.path));
}
function writeManifest(dir, version) {
  const file = path.join(dir, '文件清单.json'); assertAbsent(file);
  const content = { version, files: manifestFiles(dir) };
  fs.writeFileSync(file, JSON.stringify(content, null, 2) + '\n', { flag:'wx' }); return hashFile(file);
}
function verifyManifest(dir, version, hash) {
  const file = path.join(dir, '文件清单.json');
  if (!fs.existsSync(file) || hashFile(file) !== hash) throw Error('文件清单哈希不一致');
  const recorded = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (recorded.version !== version || JSON.stringify(recorded.files) !== JSON.stringify(manifestFiles(dir))) throw Error('产品文件与清单不一致');
}
function validateReceipt(receipt, version, attempt, manifestHash) {
  if (receipt.version !== version || receipt.attempt !== attempt || receipt.manifestSha256 !== manifestHash) throw Error('验收记录与本次产品不匹配');
  if (!Array.isArray(receipt.checks) || receipt.checks.length !== REQUIRED.length) throw Error('验收记录缺少完整功能清单');
  const names = new Set();
  for (const c of receipt.checks) {
    if (!REQUIRED.includes(c.name) || names.has(c.name) || !['passed','not_applicable'].includes(c.status) || typeof c.evidence !== 'string' || !c.evidence.trim()) throw Error('验收项缺失、未完成或证据为空：' + c.name);
    names.add(c.name);
  }
  if (!receipt.checkedAt || !receipt.confirmedBy) throw Error('缺少验收时间或确认来源');
}
function finalize(base, attempt) {
  const version = readVersion(base), p = paths(base, version, attempt); assertSafeAncestors(base,p.work); assertSafeAncestors(base,p.final); assertAbsent(p.final);
  const product = path.join(p.work, 'product'); const stateFile = path.join(p.work, '准备结果.json');
  const state = JSON.parse(fs.readFileSync(stateFile, 'utf8'));
  if (state.status !== 'prepared' || state.version !== version || state.attempt !== attempt) throw Error('准备记录不匹配');
  const receipt = JSON.parse(fs.readFileSync(path.join(p.work, '验收记录.json'), 'utf8'));
  verifyManifest(product, version, state.manifestSha256); validateReceipt(receipt, version, attempt, state.manifestSha256);
  fs.mkdirSync(path.dirname(p.final), { recursive:true }); assertAbsent(p.final);
  fs.renameSync(product, p.final);
  verifyManifest(p.final, version, state.manifestSha256);
  return p.final;
}
function run(command,args,cwd,env) {
  const result = spawnSync(command,args,{cwd,env,stdio:'inherit',shell:false});
  if (result.error) throw result.error; if (result.status !== 0) throw Error(`命令失败：${path.basename(command)}，退出码 ${result.status}`);
}
function prepare(base, attempt, options = {}) {
  const version = readVersion(base), p = paths(base, version, attempt); assertSafeAncestors(base,p.work); assertSafeAncestors(base,p.final); assertAbsent(p.work); assertAbsent(p.final);
  const python = path.join(base,'backend','.venv','Scripts','python.exe');
  const vite = path.join(base,'node_modules','vite','bin','vite.js');
  const builder = path.join(base,'node_modules','electron-builder','cli.js');
  for (const file of [python,vite,builder,path.join(base,'node_modules','electron','checksums.json')]) if (!fs.existsSync(file)) throw Error('缺少本地构建依赖，不会下载：'+file);
  const expected = require(path.join(base,'node_modules','electron','package.json')).version;
  if (!/^\d+\.\d+\.\d+$/.test(expected)) throw Error('本地 Electron 版本无效');
  const archiveName=`electron-v${expected}-win32-x64.zip`;
  const checksum=require(path.join(base,'node_modules','electron','checksums.json'))[archiveName];
  const localAppData = options.localAppData || process.env.LOCALAPPDATA;
  if (!localAppData || !path.isAbsolute(localAppData)) throw Error('本地缓存根目录不可用');
  const archive=findTrustedElectronArchive(path.join(localAppData, 'electron','Cache'),archiveName,checksum);
  fs.mkdirSync(path.dirname(p.work),{recursive:true}); fs.mkdirSync(p.work);
  const electronDist=path.join(p.work,'electron-cache'); fs.mkdirSync(electronDist);
  const copiedArchive=path.join(electronDist,archiveName); fs.copyFileSync(archive,copiedArchive,fs.constants.COPYFILE_EXCL);
  if(hashFile(copiedArchive).toLowerCase()!==checksum.toLowerCase()) throw Error('隔离 Electron 缓存 SHA-256 不匹配');
  const env = {...process.env, npm_config_offline:'true', ELECTRON_MIRROR:'http://127.0.0.1:9/', HTTP_PROXY:'http://127.0.0.1:9', HTTPS_PROXY:'http://127.0.0.1:9', http_proxy:'http://127.0.0.1:9', https_proxy:'http://127.0.0.1:9', NO_PROXY:'', no_proxy:''};
  const frontend = path.join(p.work,'frontend-build'); const backend = path.join(p.work,'backend'); const output = path.join(p.work,'builder-output');
  const runner = options.run || run;
  runner(process.execPath,[vite,'build','--config','frontend/vite.config.ts','--outDir',frontend],base,env);
  runner(python,['-m','PyInstaller','--distpath',backend,'--workpath',path.join(p.work,'pyinstaller'),'backend/sequential_runner_backend.spec'],base,env);
  const backendExe = path.join(backend,'sequential-runner-backend.exe'); if (!fs.existsSync(backendExe)) throw Error('后端构建结果不存在');
  const config = JSON.parse(fs.readFileSync(path.join(base,'scripts','config','electron-builder.validation.json'),'utf8'));
  config.directories = { output }; config.electronDist = electronDist; config.extraMetadata = { version:version.slice(1) };
  config.files = ['electron/**','package.json','VERSION.md','THIRD_PARTY_NOTICES.md',{from:frontend,to:'frontend/dist',filter:['**/*']}];
  config.extraResources = [{from:backendExe,to:'backend/sequential-runner-backend.exe'}];
  config.win = {...config.win,target:['dir'],signAndEditExecutable:false};
  const configPath = path.join(p.work,'builder-config.json'); fs.writeFileSync(configPath,JSON.stringify(config,null,2),{flag:'wx'});
  runner(process.execPath,[builder,'--win','--x64','--dir','--publish','never','--config',configPath],base,env);
  const unpacked = path.join(output,'win-unpacked'); const product = path.join(p.work,'product');
  const productName = config.productName || '顺序任务调度器';
  for (const required of [path.join(unpacked,productName+'.exe'),path.join(unpacked,'resources','app.asar'),path.join(unpacked,'resources','backend','sequential-runner-backend.exe')]) if (!fs.existsSync(required)) throw Error('免安装产品缺少必要文件：'+required);
  fs.renameSync(unpacked,product); const manifestSha256 = writeManifest(product,version);
  fs.writeFileSync(path.join(p.work,'准备结果.json'),JSON.stringify({status:'prepared',version,attempt,manifestSha256},null,2),{flag:'wx'});
  fs.writeFileSync(path.join(p.work,'验收记录.json'),JSON.stringify({version,attempt,manifestSha256,checkedAt:'',confirmedBy:'',checks:REQUIRED.map(name=>({name,status:'unverified',evidence:''}))},null,2),{flag:'wx'});
  console.log('构建准备完成，尚未验收；请按人工验收流程核查并填写验收记录：'+p.work);
}
function main() {
  const [action,...args] = process.argv.slice(2); const idx=args.indexOf('--attempt'); const attempt=idx>=0?args[idx+1]:null;
  if (!args.includes('--confirm-packaging')) throw Error('必须在已获用户明确打包授权后传入 --confirm-packaging');
  if (action==='prepare') prepare(root,attempt); else if(action==='finalize') console.log('验证目录：'+finalize(root,attempt)); else throw Error('使用 prepare 或 finalize，并提供 --attempt <标识>');
}
if(require.main===module) { try { main(); } catch(e) { console.error('打包流程停止：'+e.message); process.exitCode=1; } }
module.exports={REQUIRED,paths,assertSafeAncestors,findTrustedElectronArchive,manifestFiles,writeManifest,verifyManifest,validateReceipt,prepare,finalize};
