const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { collectFiles, createArchive, readSettings, writeSettings } = require('./settings.cjs');

function workspace() { return fs.mkdtempSync(path.join(os.tmpdir(), 'runner-settings-')); }
function cleanup(directory) { fs.rmSync(directory, { recursive: true, force: true }); }
function archiveEntries(destination) {
  const archive = fs.readFileSync(destination); const entries = new Map(); let offset = 0;
  while (offset + 4 <= archive.length && archive.readUInt32LE(offset) === 0x04034b50) {
    const method = archive.readUInt16LE(offset + 8); const size = archive.readUInt32LE(offset + 18); const nameSize = archive.readUInt16LE(offset + 26); const extraSize = archive.readUInt16LE(offset + 28);
    assert.equal(method, 0, '本地 ZIP 导出应使用可直接核验的存储条目');
    const nameStart = offset + 30; const dataStart = nameStart + nameSize + extraSize;
    entries.set(archive.subarray(nameStart, nameStart + nameSize).toString('utf8'), archive.subarray(dataStart, dataStart + size));
    offset = dataStart + size;
  }
  assert.ok(entries.size > 0, '导出 ZIP 应至少含有导出清单');
  return entries;
}
function writeFixture(directory) {
  fs.mkdirSync(path.join(directory, 'backups')); fs.mkdirSync(path.join(directory, 'logs')); fs.mkdirSync(path.join(directory, 'detail_logs'));
  fs.writeFileSync(path.join(directory, 'config.json'), JSON.stringify({ schema_version: 3, tasks: [{ id: 'maa-task', preset_id: 'maa', name: '目标 MAA' }, { id: 'other-task', preset_id: 'maaend', name: '无关 MaaEnd' }], queues: [{ id: 'maa-queue', task_ids: ['maa-task'] }, { id: 'other-queue', task_ids: ['other-task'] }], plans: [{ id: 'mixed-plan', items: [{ script_id: 'maa-queue' }, { script_id: 'other-queue' }] }] }));
  fs.writeFileSync(path.join(directory, 'history.json'), JSON.stringify({ runs: [{ id: 'mixed-run', items: [{ task_id: 'maa-task', state: 'completed' }, { task_id: 'other-task', state: 'failed' }] }] }));
  fs.writeFileSync(path.join(directory, 'settings.json'), JSON.stringify({ autoStart: false }));
  fs.writeFileSync(path.join(directory, 'backups', 'legacy.json'), '{"backup":true}');
  fs.writeFileSync(path.join(directory, 'logs', 'backend.log'), '受管后端日志');
  fs.writeFileSync(path.join(directory, 'detail_logs', 'run.log'), '受管明细日志');
}
function entryNames(entries) { return [...entries.keys()].sort(); }

test('settings use safe defaults and atomically persist supported values', () => {
  const directory = workspace();
  try {
    assert.deepEqual(readSettings(directory), { autoStart: false, minimizeOnLaunch: false, notifications: { completed: true, failed: true } });
    assert.deepEqual(writeSettings(directory, { autoStart: true, minimizeOnLaunch: true, notifications: { completed: false, failed: true }, ignored: 'no' }), { autoStart: true, minimizeOnLaunch: true, notifications: { completed: false, failed: true } });
    assert.deepEqual(readSettings(directory), { autoStart: true, minimizeOnLaunch: true, notifications: { completed: false, failed: true } });
    assert.equal(fs.readdirSync(directory).some((name) => name.endsWith('.tmp')), false);
  } finally { cleanup(directory); }
});

test('archive collection stays inside the managed data directory', () => {
  const directory = workspace(); const outside = workspace();
  try {
    fs.mkdirSync(path.join(directory, 'logs')); fs.writeFileSync(path.join(directory, 'logs', 'backend.log'), 'local'); fs.writeFileSync(path.join(directory, 'config.json'), '{}');
    assert.deepEqual(collectFiles(directory, 'backup').map((item) => item.name), ['config.json']);
    assert.deepEqual(collectFiles(directory, 'logs').map((item) => item.name), ['logs/backend.log']);
    const link = path.join(directory, 'detail_logs');
    try { fs.symlinkSync(outside, link, 'junction'); assert.throws(() => collectFiles(directory, 'logs'), /受管目录以外/); } catch (error) { if (!/受管目录以外/.test(String(error))) throw error; }
  } finally { cleanup(directory); cleanup(outside); }
});

test('backup and log archives contain only their declared managed data with a matching manifest', () => {
  const directory = workspace(); const destination = path.join(directory, 'backup.zip');
  try {
    writeFixture(directory);
    const result = createArchive(directory, destination, { kind: 'backup' });
    const backup = archiveEntries(destination);
    assert.equal(result.files, 4); assert.deepEqual(entryNames(backup), ['backups/legacy.json', 'config.json', 'history.json', 'settings.json', '导出清单.json']);
    const backupManifest = JSON.parse(backup.get('导出清单.json').toString('utf8'));
    assert.deepEqual(backupManifest.files, ['backups/legacy.json', 'config.json', 'history.json', 'settings.json']);
    assert.throws(() => createArchive(directory, destination, { kind: 'backup' }), /已经存在/);
    const logDestination = path.join(directory, 'logs.zip'); createArchive(directory, logDestination, { kind: 'logs' });
    const logs = archiveEntries(logDestination);
    assert.deepEqual(entryNames(logs), ['detail_logs/run.log', 'logs/backend.log', '导出清单.json']);
    assert.equal(logs.has('config.json'), false); assert.equal(logs.has('history.json'), false);
  } finally { cleanup(directory); }
});

test('diagnostic archive requires a preset and filters unrelated configuration and history records', () => {
  const directory = workspace(); const destination = path.join(directory, 'diagnostic.zip');
  try {
    writeFixture(directory);
    assert.throws(() => createArchive(directory, destination, { kind: 'diagnostic' }), /必须指定预设/);
    createArchive(directory, destination, { kind: 'diagnostic', presetId: 'maa' });
    const diagnostic = archiveEntries(destination);
    assert.deepEqual(entryNames(diagnostic), ['config.json', 'history.json', 'settings.json', '导出清单.json']);
    const configuration = JSON.parse(diagnostic.get('config.json').toString('utf8')); const history = JSON.parse(diagnostic.get('history.json').toString('utf8')); const manifest = JSON.parse(diagnostic.get('导出清单.json').toString('utf8'));
    assert.deepEqual(configuration.tasks.map((task) => task.id), ['maa-task']);
    assert.deepEqual(configuration.queues.map((queue) => queue.id), ['maa-queue']);
    assert.deepEqual(configuration.plans[0].items.map((item) => item.script_id), ['maa-queue']);
    assert.deepEqual(history.runs[0].items.map((item) => item.task_id), ['maa-task']);
    assert.equal(JSON.stringify(configuration).includes('other-task'), false); assert.equal(JSON.stringify(history).includes('other-task'), false);
    assert.deepEqual(manifest.files, ['config.json', 'history.json', 'settings.json']);
  } finally { cleanup(directory); }
});

test('diagnostic export rejects invalid managed JSON and empty log export still writes only its manifest', () => {
  const directory = workspace(); const diagnosticDestination = path.join(directory, 'diagnostic.zip'); const logDestination = path.join(directory, 'empty-logs.zip');
  try {
    fs.writeFileSync(path.join(directory, 'config.json'), '{invalid');
    assert.throws(() => createArchive(directory, diagnosticDestination, { kind: 'diagnostic', presetId: 'maa' }), /无法生成预设问题包/);
    fs.rmSync(path.join(directory, 'config.json'));
    createArchive(directory, logDestination, { kind: 'logs' });
    assert.deepEqual(entryNames(archiveEntries(logDestination)), ['导出清单.json']);
  } finally { cleanup(directory); }
});
