const { app, BrowserWindow, clipboard, dialog, ipcMain, Menu, Notification, safeStorage, shell } = require('electron');
const { spawn, execFile } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { promisify } = require('node:util');
const { prepareDataDirectory } = require('./portable-data.cjs');
const { createArchive, readSettings, writeSettings } = require('./settings.cjs');
const { store: webhookStore, send: sendWebhook } = require('./webhooks.cjs');
const { displayedVersion } = require('./version.cjs');
const { createQQNotifications } = require('./qq-notifications.cjs');

let backend; let windowRef; let connection; let quitting = false; let backendState = 'starting'; let backendDetail = '';
const sourceRoot = path.resolve(__dirname, '..');
function developmentVersion() {
  return displayedVersion({ packaged: app.isPackaged, sourceRoot, appPath: app.getAppPath() });
}
const executableNames = { maa: ['maa.exe'], zzz_onedragon: ['onedragon-runtimelauncher.exe', 'onedragon-launcher.exe'], maaend: ['maaend.exe'], march7th: ['march7th assistant.exe'], bettergi: ['bettergi.exe'] };
function frontendEntry() { return app.isPackaged ? path.join(app.getAppPath(), 'frontend', 'dist', 'index.html') : path.join(sourceRoot, 'frontend', 'dist', 'index.html'); }
function runtimeDataDirectory() { return prepareDataDirectory({ packaged: app.isPackaged, sourceRoot, execPath: process.execPath, environment: process.env }); }
function uiWindowDimension(name, fallback, minimum) { const value = Number.parseInt(process.env[name] || '', 10); return Number.isInteger(value) && value >= minimum && value <= 4096 ? value : fallback; }
app.setPath('userData', path.join(runtimeDataDirectory(), 'electron'));
let appSettings = readSettings(runtimeDataDirectory());
let qqManager = null;
function qq() { if (!qqManager) qqManager = createQQNotifications({ directory: runtimeDataDirectory(), secure: safeStorage }); return qqManager; }
function applyLoginSettings(settings) {
  if (process.platform === 'win32') app.setLoginItemSettings({ openAtLogin: settings.autoStart, openAsHidden: settings.autoStart && settings.minimizeOnLaunch });
}
function backendCommand() { if (app.isPackaged) return { command: path.join(process.resourcesPath, 'backend', 'sequential-runner-backend.exe') }; const base = path.join(sourceRoot, 'backend'); return { command: path.join(base, '.venv', 'Scripts', 'python.exe'), args: ['-m', 'app.runner_server'], cwd: base }; }
function startBackend() {
  const target = backendCommand(); const token = crypto.randomUUID();
  backendState = 'starting'; backendDetail = '';
  backend = spawn(target.command, [...(target.args || []), '--data-dir', runtimeDataDirectory(), '--instance-id', token], { cwd: target.cwd || path.dirname(target.command), stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  return new Promise((resolve, reject) => {
    let output = ''; const timer = setTimeout(() => reject(new Error('后端未在 15 秒内就绪。请检查依赖、端口与 data 目录权限。')), 15000);
    const fail = (detail) => { backendState = 'failed'; backendDetail = detail; clearTimeout(timer); reject(new Error(`本地后端启动失败：${detail}`)); };
    backend.once('error', (error) => fail(error.message));
    backend.once('exit', (code) => { if (!quitting) { backendState = 'stopped'; backendDetail = `后端进程已退出（${code ?? '未知'}）。`; connection = undefined; if (windowRef) windowRef.webContents.send('runner:backend-state', { state: backendState, detail: backendDetail }); } if (!connection) fail(backendDetail); });
    backend.stdout.on('data', (chunk) => { output += chunk.toString(); const ready = output.match(/RUNNER_READY (\{[^\r\n]+\})/); if (!ready) return; try { const info = JSON.parse(ready[1]); connection = { apiBase: `http://127.0.0.1:${info.port}`, wsBase: `ws://127.0.0.1:${info.port}`, instanceId: token }; backendState = 'ready'; clearTimeout(timer); resolve(); } catch (error) { fail(`就绪信息无效：${error.message}`); } });
    backend.stderr.on('data', (chunk) => { output = `${output}\n${chunk}`.slice(-4000); backendDetail = output; });
  });
}
async function verifyBackend() { const response = await fetch(`${connection.apiBase}/health`); const body = await response.json(); if (!response.ok || body.instanceId !== connection.instanceId) throw new Error('未确认本次启动的后端实例。'); }
async function stopBackend() {
  if (!backend) return;
  const backendPid = backend.pid;
  try { if (connection) await fetch(`${connection.apiBase}/api/stop`, { method: 'POST', signal: AbortSignal.timeout(2500) }); } catch (_) {}
  // PyInstaller's windowless bootloader can keep a child alive after its launcher exits.
  // Scope taskkill to this Electron-created root PID and its tree; never search by name.
  if (process.platform === 'win32' && backendPid) {
    try { await promisify(execFile)('taskkill', ['/PID', String(backendPid), '/T', '/F'], { windowsHide: true }); } catch (_) {}
  } else if (!backend.killed) backend.kill();
  backend = undefined;
  connection = undefined;
}
function shutdown() {
  if (quitting) return;
  quitting = true;
  void (async () => {
    if (qqManager) qqManager.stop();
    await stopBackend();
    app.exit(0);
  })();
}
ipcMain.handle('runner:get-connection', () => connection || null);
ipcMain.handle('runner:get-backend-status', () => ({ state: backendState, detail: backendDetail }));
ipcMain.handle('runner:get-build-info', () => ({ version: developmentVersion() }));
ipcMain.handle('runner:get-settings', () => appSettings);
ipcMain.handle('runner:save-settings', (_event, next) => {
  appSettings = writeSettings(runtimeDataDirectory(), next);
  applyLoginSettings(appSettings);
  return appSettings;
});
ipcMain.handle('runner:export-archive', async (_event, request) => {
  const kind = request?.kind;
  if (!['backup', 'logs', 'diagnostic'].includes(kind)) throw new Error('导出类型无效。');
  const label = kind === 'backup' ? '数据备份' : kind === 'logs' ? '本体日志' : '专项问题包';
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const picked = await dialog.showSaveDialog(windowRef, { defaultPath: `${label}-${stamp}.zip`, filters: [{ name: 'ZIP 文件', extensions: ['zip'] }] });
  if (picked.canceled || !picked.filePath) return { canceled: true };
  const result = createArchive(runtimeDataDirectory(), picked.filePath, { kind, presetId: typeof request.presetId === 'string' ? request.presetId : null });
  return { canceled: false, ...result };
});
ipcMain.handle('runner:show-notification', async (_event, request) => {
  const kind = request?.kind;
  if (!['completed', 'failed'].includes(kind)) throw new Error('通知类型无效。');
  if (!appSettings.notifications[kind]) return { shown: false };
  const message = { title: typeof request.title === 'string' ? request.title.slice(0, 120) : '顺序任务调度器', body: typeof request.body === 'string' ? request.body.slice(0, 400) : '' };
  return require('./notification-dispatch.cjs').dispatchNotifications({
    system: () => { if (!appSettings.notifications.system || !Notification.isSupported()) return false; new Notification(message).show(); return true; },
    webhooks: async () => { const hooks = webhookStore(runtimeDataDirectory(), safeStorage); const results = await Promise.allSettled(hooks.list().filter(value => value.enabled).map(item => Promise.resolve().then(() => sendWebhook(hooks.get(item.id), message)))); return results.filter(result => result.status === 'rejected').length; },
    qq: async () => { if (qq().status().enabled) await qq().send(message); },
  });
});
ipcMain.handle('runner:qq-status', () => qq().status());
ipcMain.handle('runner:qq-start', () => qq().start());
ipcMain.handle('runner:qq-poll', (_event, id) => qq().poll(id));
ipcMain.handle('runner:qq-cancel', () => { qq().cancel(); return qq().status(); });
ipcMain.handle('runner:qq-enable', (_event, value) => { qq().setEnabled(value); return qq().status(); });
ipcMain.handle('runner:qq-unbind', () => { qq().unbind(); return qq().status(); });
ipcMain.handle('runner:qq-test', () => qq().send({ title:'顺序任务调度器测试通知', body:'这是一条测试通知。' }));
ipcMain.handle('runner:webhooks-list', () => webhookStore(runtimeDataDirectory(), safeStorage).list());
ipcMain.handle('runner:webhooks-get', (_event, id) => webhookStore(runtimeDataDirectory(), safeStorage).get(id));
ipcMain.handle('runner:webhooks-save', (_event, value) => webhookStore(runtimeDataDirectory(), safeStorage).save(value));
ipcMain.handle('runner:webhooks-remove', (_event, id) => { webhookStore(runtimeDataDirectory(), safeStorage).remove(id); return { removed: true }; });
ipcMain.handle('runner:webhooks-reset-unreadable', () => { webhookStore(runtimeDataDirectory(), safeStorage).resetUnreadable(); return { reset: true }; });
ipcMain.handle('runner:webhooks-test', async (_event, id) => sendWebhook(webhookStore(runtimeDataDirectory(), safeStorage).get(id), { title: '顺序任务调度器测试通知', body: '这是一条测试通知。' }));
ipcMain.handle('runner:system-notification-test', () => { if (!Notification.isSupported()) return { shown: false }; new Notification({ title: '顺序任务调度器测试通知', body: '这是一条测试通知。' }).show(); return { shown: true }; });
ipcMain.handle('runner:select-executable', async (_event, presetId) => { const picked = await dialog.showOpenDialog(windowRef, { properties: ['openFile'], filters: [{ name: 'Windows 可执行程序', extensions: ['exe'] }] }); if (picked.canceled || !picked.filePaths[0]) return { canceled: true }; const selected = picked.filePaths[0]; const expected = executableNames[presetId] || []; return { canceled: false, path: selected, matchesPreset: !presetId || expected.includes(path.basename(selected).toLowerCase()) }; });
ipcMain.handle('runner:select-directory', async () => { const picked = await dialog.showOpenDialog(windowRef, { properties: ['openDirectory'] }); return picked.canceled || !picked.filePaths[0] ? { canceled: true } : { canceled: false, path: picked.filePaths[0] }; });
function externalFile(source) {
  if (typeof source !== 'string' || !path.isAbsolute(source) || !fs.existsSync(source) || !fs.statSync(source).isFile()) throw new Error('外部日志文件不存在或不可读取。');
  return source;
}
async function authorizedHistoryFile(reference) {
  if (!connection || !reference || typeof reference.runId !== 'string' || typeof reference.taskId !== 'string' || !Number.isInteger(reference.index)) throw new Error('外部日志引用无效。');
  const endpoint = `${connection.apiBase}/api/history/${encodeURIComponent(reference.runId)}/external-logs/${encodeURIComponent(reference.taskId)}`;
  const response = await fetch(endpoint);
  if (!response.ok) throw new Error('外部日志引用已失效。');
  const body = await response.json();
  const file = body.files?.find((candidate) => candidate.index === reference.index && candidate.available);
  if (!file?.path) throw new Error('外部日志文件不存在或不可读取。');
  return externalFile(file.path);
}
ipcMain.handle('runner:copy-text', (_event, text) => { if (typeof text !== 'string') throw new Error('复制内容无效。'); if (Buffer.byteLength(text, 'utf8') > 10 * 1024 * 1024) throw new Error('日志内容超过 10 MiB，请使用导出。'); clipboard.writeText(text); return { accepted: true }; });
ipcMain.handle('runner:export-file', async (_event, reference) => { const file = await authorizedHistoryFile(reference); const picked = await dialog.showSaveDialog(windowRef, { defaultPath: path.basename(file) }); if (picked.canceled || !picked.filePath) return { canceled: true }; fs.copyFileSync(file, picked.filePath, fs.constants.COPYFILE_EXCL); return { canceled: false, path: picked.filePath }; });
ipcMain.handle('runner:show-in-folder', async (_event, reference) => { shell.showItemInFolder(await authorizedHistoryFile(reference)); return { accepted: true }; });
ipcMain.handle('runner:quit-app', () => { shutdown(); return { accepted: true }; });
async function createWindow() { await startBackend(); await verifyBackend(); Menu.setApplicationMenu(null); const healthCheck = process.env.RUNNER_HEALTH_CHECK === '1'; const width = uiWindowDimension('RUNNER_UI_WINDOW_WIDTH', 1280, 640); const height = uiWindowDimension('RUNNER_UI_WINDOW_HEIGHT', 820, 620); windowRef = new BrowserWindow({ width, height, minWidth: 640, minHeight: 620, show: false, backgroundColor: '#0b1722', title: `顺序任务调度器 ${developmentVersion()}`, webPreferences: { contextIsolation: true, sandbox: true, nodeIntegration: false, preload: path.join(__dirname, 'preload.cjs') } }); windowRef.webContents.setWindowOpenHandler(() => ({ action: 'deny' })); windowRef.once('ready-to-show', () => { if (healthCheck) return; windowRef.show(); if (appSettings.minimizeOnLaunch) windowRef.minimize(); }); const devServer = process.env.VITE_DEV_SERVER_URL; if (devServer) await windowRef.loadURL(devServer); else await windowRef.loadFile(frontendEntry()); if (healthCheck) setTimeout(shutdown, 1000); }
const lock = app.requestSingleInstanceLock(); if (!lock) app.quit();
app.on('second-instance', () => { if (windowRef) { if (windowRef.isMinimized()) windowRef.restore(); windowRef.focus(); } });
app.whenReady().then(async () => { try { qq().startGateway(); } catch (_) {} await createWindow(); }).catch(async (error) => { await dialog.showMessageBox({ type: 'error', title: '启动失败', message: error.message }); app.quit(); });
app.on('before-quit', (event) => { if (quitting) return; event.preventDefault(); shutdown(); });
app.on('window-all-closed', () => app.quit());
