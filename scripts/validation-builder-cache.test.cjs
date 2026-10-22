const assert = require('node:assert/strict');
const { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { buildFileManifest, prepareValidationBuilderCache } = require('./validation-builder-cache.cjs');

function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'validation-builder-cache-'));
  const sourceCacheRoot = join(directory, 'source-cache');
  const sourceDirectory = join(sourceCacheRoot, 'nsis', '817365875');
  const cacheRoot = join(directory, 'work', 'cache', 'builder');
  const evidencePath = join(directory, 'work', 'builder-cache-preparation.json');
  mkdirSync(join(sourceDirectory, 'Bin'), { recursive: true });
  writeFileSync(join(sourceDirectory, 'Bin', 'makensis.exe'), 'fixture executable');
  writeFileSync(join(sourceDirectory, 'payload.dat'), 'fixture payload');
  return { directory, sourceCacheRoot, sourceDirectory, cacheRoot, evidencePath };
}

function run(name, action) {
  action();
  console.log('PASS', name);
}

run('复制受信 NSIS 缓存到隔离目录', () => {
  const item = fixture();
  try {
    const expectedManifestSha256 = buildFileManifest(item.sourceDirectory).sha256;
    const evidence = prepareValidationBuilderCache({ ...item, expectedManifestSha256 });
    assert.equal(existsSync(join(item.cacheRoot, 'nsis', '817365875', 'payload.dat')), true);
    assert.equal(evidence.targetManifest.sha256, expectedManifestSha256);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('源缓存指纹不符时拒绝复制', () => {
  const item = fixture();
  try {
    assert.throws(() => prepareValidationBuilderCache({ ...item, expectedManifestSha256: '0'.repeat(64) }), /文件清单与受信指纹不一致/);
    assert.equal(existsSync(join(item.cacheRoot, 'nsis', '817365875')), false);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('源缓存缺失时拒绝复制', () => {
  const item = fixture();
  try {
    rmSync(item.sourceDirectory, { recursive: true, force: true });
    assert.throws(() => prepareValidationBuilderCache({ ...item }), /本机缓存缺少 NSIS 目录/);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});

run('目标目录已存在时拒绝覆盖', () => {
  const item = fixture();
  try {
    const target = join(item.cacheRoot, 'nsis', '817365875');
    mkdirSync(target, { recursive: true });
    assert.throws(() => prepareValidationBuilderCache({ ...item }), /隔离缓存目标已存在/);
  } finally { rmSync(item.directory, { recursive: true, force: true }); }
});
