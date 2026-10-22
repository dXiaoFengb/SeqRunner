const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { dirname, join } = require('node:path');
const { Cache } = require('@electron/get/dist/cjs/Cache');
const { prepareValidationElectronCache } = require('./validation-electron-cache.cjs');

const version = '37.2.5';
const fileName = `electron-v${version}-win32-x64.zip`;
const downloadUrl = `https://github.com/electron/electron/releases/download/v${version}/${fileName}`;
const cacheKey = Cache.getCacheDirectory(downloadUrl);

function hash(content) {
  return createHash('sha256').update(content).digest('hex');
}

function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'validation-electron-cache-'));
  const root = join(directory, 'project');
  const sourceCacheRoot = join(directory, 'source-cache');
  const cacheRoot = join(directory, 'work', 'cache', 'electron');
  const evidencePath = join(directory, 'work', 'electron-cache-preparation.json');
  const outputDirectory = join(directory, 'work', 'electron');
  const content = Buffer.from('fixture Electron zip contents');
  const sourcePath = join(sourceCacheRoot, cacheKey, fileName);
  mkdirSync(join(root, 'node_modules', 'electron'), { recursive: true });
  mkdirSync(join(sourceCacheRoot, cacheKey), { recursive: true });
  writeFileSync(join(root, 'node_modules', 'electron', 'package.json'), JSON.stringify({ version }));
  writeFileSync(join(root, 'node_modules', 'electron', 'checksums.json'), JSON.stringify({ [fileName]: hash(content) }));
  writeFileSync(sourcePath, content);
  return { directory, root, sourceCacheRoot, cacheRoot, evidencePath, outputDirectory, sourcePath, content };
}

function run(name, action) {
  action();
  console.log('PASS', name);
}

run('复制正确缓存键、校验哈希并不写 Electron 输出目录', () => {
  const item = fixture();
  try {
    const evidence = prepareValidationElectronCache(item);
    const targetPath = join(item.cacheRoot, cacheKey, fileName);
    assert.equal(existsSync(targetPath), true);
    assert.deepEqual(readFileSync(targetPath), item.content);
    assert.equal(existsSync(item.outputDirectory), false);
    assert.equal(evidence.cacheKey, cacheKey);
    assert.equal(dirname(evidence.targetPath), join(item.cacheRoot, cacheKey));
    assert.equal(JSON.parse(readFileSync(item.evidencePath, 'utf8')).targetPath, targetPath);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('哈希不符时拒绝复制', () => {
  const item = fixture();
  try {
    writeFileSync(item.sourcePath, 'tampered');
    assert.throws(() => prepareValidationElectronCache(item), /SHA-256 与 electron\/checksums\.json 不一致/);
    assert.equal(existsSync(join(item.cacheRoot, cacheKey, fileName)), false);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('源缓存缺失时拒绝复制', () => {
  const item = fixture();
  try {
    rmSync(item.sourcePath);
    assert.throws(() => prepareValidationElectronCache(item), /本机缓存缺少同版本 ZIP/);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('目标文件已存在时拒绝覆盖', () => {
  const item = fixture();
  try {
    const targetPath = join(item.cacheRoot, cacheKey, fileName);
    mkdirSync(join(item.cacheRoot, cacheKey), { recursive: true });
    writeFileSync(targetPath, item.content);
    assert.throws(() => prepareValidationElectronCache(item), /隔离缓存目标已存在/);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});
