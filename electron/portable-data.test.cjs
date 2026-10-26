const assert = require('node:assert/strict');
const { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join, normalize } = require('node:path');
const test = require('node:test');
const { dataDirectory, prepareDataDirectory } = require('./portable-data.cjs');

test('development data remains inside the project source root', () => {
  assert.equal(dataDirectory({ packaged: false, sourceRoot: 'C:/project', execPath: 'C:/ignored.exe', environment: {} }), normalize('C:/project/data'));
});

test('an explicit development preview directory is isolated and does not migrate project data', () => {
  const root = mkdtempSync(join(tmpdir(), 'runner-preview-data-'));
  try {
    const preview = join(root, '产物', '预览', '批次', 'run-1', 'UserData');
    mkdirSync(join(root, 'data'), { recursive: true });
    writeFileSync(join(root, 'data', 'config.json'), '{"user":true}', 'utf8');
    assert.equal(prepareDataDirectory({ packaged: false, sourceRoot: root, execPath: join(root, 'electron.exe'), environment: { RUNNER_USER_DATA_DIR: preview } }), preview);
    assert.equal(require('node:fs').existsSync(join(preview, 'config.json')), false);
    assert.equal(readFileSync(join(root, 'data', 'config.json'), 'utf8'), '{"user":true}');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('portable data migrates only supported legacy entries without deleting them', () => {
  const root = mkdtempSync(join(tmpdir(), 'runner-portable-data-'));
  try {
    const legacy = join(root, 'data');
    mkdirSync(join(legacy, 'backups'), { recursive: true });
    mkdirSync(join(legacy, 'detail_logs'), { recursive: true });
    writeFileSync(join(legacy, 'config.json'), '{"theme":"dark"}', 'utf8');
    writeFileSync(join(legacy, 'history.json'), '{"runs":[]}', 'utf8');
    writeFileSync(join(legacy, 'backups', 'history.json.bak'), 'backup', 'utf8');
    writeFileSync(join(legacy, 'detail_logs', 'old.jsonl'), 'detail', 'utf8');
    writeFileSync(join(legacy, 'unrelated.txt'), 'leave out', 'utf8');
    const target = prepareDataDirectory({ packaged: true, sourceRoot: 'ignored', execPath: join(root, 'runner.exe'), environment: {} });
    assert.equal(target, join(root, 'UserData'));
    assert.equal(readFileSync(join(target, 'config.json'), 'utf8'), '{"theme":"dark"}');
    assert.equal(readFileSync(join(target, 'detail_logs', 'old.jsonl'), 'utf8'), 'detail');
    assert.equal(readFileSync(join(legacy, 'config.json'), 'utf8'), '{"theme":"dark"}');
    assert.equal(require('node:fs').existsSync(join(target, 'unrelated.txt')), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('an existing UserData folder takes precedence over older portable folders', () => {
  const root = mkdtempSync(join(tmpdir(), 'runner-portable-data-'));
  try {
    const target = join(root, 'UserData'); const legacy = join(root, '用户数据'); const oldest = join(root, 'data');
    mkdirSync(target); mkdirSync(legacy); mkdirSync(oldest);
    writeFileSync(join(target, 'config.json'), 'new', 'utf8'); writeFileSync(join(legacy, 'config.json'), 'old', 'utf8');
    assert.equal(prepareDataDirectory({ packaged: true, sourceRoot: 'ignored', execPath: join(root, 'runner.exe'), environment: {} }), target);
    assert.equal(readFileSync(join(target, 'config.json'), 'utf8'), 'new');
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('the prior Chinese portable folder is migrated before the oldest data folder', () => {
  const root = mkdtempSync(join(tmpdir(), 'runner-portable-data-'));
  try {
    const prior = join(root, '用户数据'); const oldest = join(root, 'data');
    mkdirSync(prior); mkdirSync(oldest);
    writeFileSync(join(prior, 'config.json'), 'prior', 'utf8'); writeFileSync(join(oldest, 'config.json'), 'oldest', 'utf8');
    const target = prepareDataDirectory({ packaged: true, sourceRoot: 'ignored', execPath: join(root, 'runner.exe'), environment: {} });
    assert.equal(readFileSync(join(target, 'config.json'), 'utf8'), 'prior');
    assert.equal(readFileSync(join(prior, 'config.json'), 'utf8'), 'prior');
    assert.equal(readFileSync(join(oldest, 'config.json'), 'utf8'), 'oldest');
  } finally { rmSync(root, { recursive: true, force: true }); }
});
