const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const DEFAULT_SETTINGS = Object.freeze({
  autoStart: false,
  minimizeOnLaunch: false,
  notifications: Object.freeze({ completed: true, failed: true }),
});

function settingsPath(dataDirectory) { return path.join(dataDirectory, 'settings.json'); }
function asBoolean(value, fallback) { return typeof value === 'boolean' ? value : fallback; }
function normalizeSettings(value) {
  const source = value && typeof value === 'object' ? value : {};
  const notifications = source.notifications && typeof source.notifications === 'object' ? source.notifications : {};
  return {
    autoStart: asBoolean(source.autoStart, DEFAULT_SETTINGS.autoStart),
    minimizeOnLaunch: asBoolean(source.minimizeOnLaunch, DEFAULT_SETTINGS.minimizeOnLaunch),
    notifications: {
      completed: asBoolean(notifications.completed, DEFAULT_SETTINGS.notifications.completed),
      failed: asBoolean(notifications.failed, DEFAULT_SETTINGS.notifications.failed),
    },
  };
}
function readSettings(dataDirectory) {
  try { return normalizeSettings(JSON.parse(fs.readFileSync(settingsPath(dataDirectory), 'utf8'))); } catch (_) { return normalizeSettings(); }
}
function writeSettings(dataDirectory, value) {
  const settings = normalizeSettings(value);
  fs.mkdirSync(dataDirectory, { recursive: true });
  const target = settingsPath(dataDirectory);
  const temporary = `${target}.${crypto.randomUUID()}.tmp`;
  try {
    fs.writeFileSync(temporary, `${JSON.stringify(settings, null, 2)}\n`, 'utf8');
    fs.renameSync(temporary, target);
  } finally {
    if (fs.existsSync(temporary)) fs.rmSync(temporary, { force: true });
  }
  return settings;
}

function isInside(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative && !relative.startsWith('..') && !path.isAbsolute(relative);
}
function archiveRoots(kind) {
  if (kind === 'backup') return ['config.json', 'history.json', 'backups', 'settings.json'];
  if (kind === 'logs') return ['logs', 'detail_logs'];
  if (kind === 'diagnostic') return ['config.json', 'history.json', 'settings.json'];
  throw new Error('未知的导出类型。');
}
function collectFiles(root, kind) {
  const canonicalRoot = fs.realpathSync(root);
  const collected = [];
  for (const name of archiveRoots(kind)) {
    const candidate = path.join(root, name);
    if (!fs.existsSync(candidate)) continue;
    const visit = (entry) => {
      const real = fs.realpathSync(entry);
      if (!isInside(canonicalRoot, real)) throw new Error('导出范围包含受管目录以外的路径。');
      const stat = fs.statSync(real);
      if (stat.isDirectory()) {
        for (const child of fs.readdirSync(real)) visit(path.join(real, child));
      } else if (stat.isFile()) {
        collected.push({ source: real, name: path.relative(canonicalRoot, real).replaceAll('\\', '/') });
      }
    };
    visit(candidate);
  }
  return collected.sort((left, right) => left.name.localeCompare(right.name));
}

let crcTable;
function crc32(buffer) {
  crcTable ||= Array.from({ length: 256 }, (_, index) => {
    let value = index;
    for (let bit = 0; bit < 8; bit += 1) value = (value >>> 1) ^ (value & 1 ? 0xedb88320 : 0);
    return value >>> 0;
  });
  let result = 0xffffffff;
  for (const byte of buffer) result = (result >>> 8) ^ crcTable[(result ^ byte) & 0xff];
  return (result ^ 0xffffffff) >>> 0;
}
function uint16(value) { const output = Buffer.alloc(2); output.writeUInt16LE(value, 0); return output; }
function uint32(value) { const output = Buffer.alloc(4); output.writeUInt32LE(value >>> 0, 0); return output; }
function zip(entries) {
  const local = []; const central = []; let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8'); const data = entry.data; const checksum = crc32(data);
    const localHeader = Buffer.concat([uint32(0x04034b50), uint16(20), uint16(0x0800), uint16(0), uint16(0), uint16(0), uint32(checksum), uint32(data.length), uint32(data.length), uint16(name.length), uint16(0), name]);
    local.push(localHeader, data);
    central.push(Buffer.concat([uint32(0x02014b50), uint16(20), uint16(20), uint16(0x0800), uint16(0), uint16(0), uint16(0), uint32(checksum), uint32(data.length), uint32(data.length), uint16(name.length), uint16(0), uint16(0), uint16(0), uint16(0), uint32(0), uint32(offset), name]));
    offset += localHeader.length + data.length;
  }
  const centralSize = central.reduce((total, value) => total + value.length, 0);
  return Buffer.concat([...local, ...central, uint32(0x06054b50), uint16(0), uint16(0), uint16(entries.length), uint16(entries.length), uint32(centralSize), uint32(offset), uint16(0)]);
}
function diagnosticPayload(file, presetId) {
  if (!presetId || !['config.json', 'history.json'].includes(file.name)) return fs.readFileSync(file.source);
  try {
    const document = JSON.parse(fs.readFileSync(file.source, 'utf8'));
    if (file.name === 'config.json') {
      const tasks = Array.isArray(document.tasks) ? document.tasks.filter((task) => task?.preset_id === presetId) : [];
      const taskIds = new Set(tasks.map((task) => task.id));
      const queues = Array.isArray(document.queues) ? document.queues.filter((queue) => Array.isArray(queue?.task_ids) && queue.task_ids.some((id) => taskIds.has(id))) : [];
      const queueIds = new Set(queues.map((queue) => queue.id));
      const plans = Array.isArray(document.plans) ? document.plans.map((plan) => ({ ...plan, items: Array.isArray(plan.items) ? plan.items.filter((item) => queueIds.has(item?.script_id)) : [] })).filter((plan) => plan.items.length) : [];
      return Buffer.from(JSON.stringify({ ...document, tasks, queues, plans }, null, 2), 'utf8');
    }
    const configured = fs.existsSync(path.join(path.dirname(file.source), 'config.json')) ? JSON.parse(fs.readFileSync(path.join(path.dirname(file.source), 'config.json'), 'utf8')) : {};
    const taskIds = new Set((configured.tasks || []).filter((task) => task?.preset_id === presetId).map((task) => task.id));
    const runs = Array.isArray(document.runs) ? document.runs.map((run) => ({ ...run, items: Array.isArray(run.items) ? run.items.filter((item) => taskIds.has(item?.task_id)) : [] })).filter((run) => run.items.length) : [];
    return Buffer.from(JSON.stringify({ ...document, runs }, null, 2), 'utf8');
  } catch (_) { throw new Error('受管配置或历史记录无效，无法生成预设问题包。'); }
}
function createArchive(dataDirectory, destination, { kind, presetId = null } = {}) {
  if (!path.isAbsolute(destination) || fs.existsSync(destination)) throw new Error('导出目标无效或已经存在。');
  if (kind === 'diagnostic' && (!presetId || typeof presetId !== 'string')) throw new Error('专项问题包必须指定预设。');
  const files = collectFiles(dataDirectory, kind);
  const manifest = Buffer.from(JSON.stringify({ format: 'sequential-runner-support-v1', kind, presetId, createdAt: new Date().toISOString(), files: files.map((file) => file.name) }, null, 2), 'utf8');
  const payload = [...files.map((file) => ({ name: file.name, data: kind === 'diagnostic' ? diagnosticPayload(file, presetId) : fs.readFileSync(file.source) })), { name: '导出清单.json', data: manifest }];
  fs.writeFileSync(destination, zip(payload), { flag: 'wx' });
  return { path: destination, files: files.length };
}

module.exports = { DEFAULT_SETTINGS, collectFiles, createArchive, normalizeSettings, readSettings, settingsPath, writeSettings, zip };
