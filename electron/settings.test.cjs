const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { collectFiles, createArchive, readSettings, writeSettings } = require('./settings.cjs');

function workspace() { return fs.mkdtempSync(path.join(os.tmpdir(), 'runner-settings-')); }
function cleanup(directory) { fs.rmSync(directory, { recursive: true, force: true }); }

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

test('archive writes a non-overwriting zip with an export manifest', () => {
  const directory = workspace(); const destination = path.join(directory, 'backup.zip');
  try {
    fs.writeFileSync(path.join(directory, 'config.json'), '{"schema_version":3}');
    const result = createArchive(directory, destination, { kind: 'backup' });
    const archive = fs.readFileSync(destination);
    assert.equal(result.files, 1); assert.equal(archive.readUInt32LE(0), 0x04034b50); assert.equal(archive.includes(Buffer.from('导出清单.json')), true);
    assert.throws(() => createArchive(directory, destination, { kind: 'backup' }), /已经存在/);
  } finally { cleanup(directory); }
});

test('diagnostic archive requires a preset and omits unrelated tasks from its JSON payload', () => {
  const directory = workspace(); const destination = path.join(directory, 'diagnostic.zip');
  try {
    fs.writeFileSync(path.join(directory, 'config.json'), JSON.stringify({ tasks: [{ id: 'maa', preset_id: 'maa' }, { id: 'other', preset_id: 'maaend' }], queues: [{ id: 'q1', task_ids: ['maa'] }, { id: 'q2', task_ids: ['other'] }], plans: [{ id: 'p1', items: [{ script_id: 'q1' }, { script_id: 'q2' }] }] }));
    fs.writeFileSync(path.join(directory, 'history.json'), JSON.stringify({ runs: [{ id: 'r1', items: [{ task_id: 'maa' }, { task_id: 'other' }] }] }));
    assert.throws(() => createArchive(directory, destination, { kind: 'diagnostic' }), /必须指定预设/);
    createArchive(directory, destination, { kind: 'diagnostic', presetId: 'maa' });
    const archive = fs.readFileSync(destination).toString('utf8');
    assert.equal(archive.includes('"other"'), false); assert.equal(archive.includes('"maa"'), true);
  } finally { cleanup(directory); }
});
