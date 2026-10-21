const assert = require('node:assert/strict');
const { existsSync, mkdtempSync, mkdirSync, rmSync, writeFileSync } = require('node:fs');
const { createHash } = require('node:crypto');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const test = require('node:test');
const {
  assertLegacySuffixIsUnused,
  assertPortableValidationReady,
  assertPreparationTargetsAvailable,
  getValidationPackagePaths,
} = require('./validation-package-paths.cjs');
const { finalizeValidationPackage } = require('./finalize-validation-package.cjs');

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'runner-validation-paths-'));
  writeFileSync(join(root, 'VERSION.md'), '- 开发提交版本：v1.2.3\n', 'utf8');
  return root;
}

test('验证包路径仅按开发版本命名', () => {
  const root = fixture();
  try {
    const paths = getValidationPackagePaths(root);
    assert.match(paths.workDirectory, /产物[\\\\/]工作[\\\\/]v1\.2\.3$/);
    assert.match(paths.verificationDirectory, /产物[\\\\/]验证[\\\\/]v1\.2\.3$/);
    assert.doesNotMatch(paths.workDirectory, /0123/);
    assertPreparationTargetsAvailable(paths);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('工作或验证目录存在时拒绝覆盖', () => {
  const root = fixture();
  try {
    const paths = getValidationPackagePaths(root);
    mkdirSync(paths.workDirectory, { recursive: true });
    assert.throws(() => assertPreparationTargetsAvailable(paths), /工作目录已存在/);
    rmSync(paths.workDirectory, { recursive: true, force: true });
    mkdirSync(paths.verificationDirectory, { recursive: true });
    assert.throws(() => assertPreparationTargetsAvailable(paths), /验证包目录已存在/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('验证 portable 阶段只能使用本轮已准备且未输出 Electron 的工作目录', () => {
  const root = fixture();
  try {
    const paths = getValidationPackagePaths(root);
    assert.throws(() => assertPortableValidationReady(paths, {}), /准备阶段/);
    mkdirSync(paths.workDirectory, { recursive: true });
    assert.throws(() => assertPortableValidationReady(paths, { RUNNER_VALIDATION_PREPARED: '1' }), /后端资源不存在/);
    mkdirSync(paths.backendDirectory, { recursive: true });
    writeFileSync(join(paths.backendDirectory, 'sequential-runner-backend.exe'), 'fixture backend', 'utf8');
    assertPortableValidationReady(paths, { RUNNER_VALIDATION_PREPARED: '1' });
    mkdirSync(paths.electronDirectory, { recursive: true });
    assert.throws(() => assertPortableValidationReady(paths, { RUNNER_VALIDATION_PREPARED: '1' }), /输出目录已存在/);
  } finally { rmSync(root, { recursive: true, force: true }); }
});

test('拒绝旧验证后缀环境变量', () => {
  assert.throws(() => assertLegacySuffixIsUnused({ RUNNER_VALIDATION_SUFFIX: '01147' }), /不再支持/);
});

test('只有匹配的通过健康记录才能最终化', () => {
  const root = fixture();
  try {
    const paths = getValidationPackagePaths(root);
    mkdirSync(paths.electronDirectory, { recursive: true });
    const executable = join(paths.electronDirectory, 'runner-v1.2.3-portable.exe');
    writeFileSync(executable, 'fixture executable', 'utf8');
    assert.throws(() => finalizeValidationPackage(paths), /缺少通过/);
    writeFileSync(join(paths.workDirectory, '.validation-health.json'), JSON.stringify({ status: 'failed', version: '1.2.3', executableSha256: 'ignored' }), 'utf8');
    assert.throws(() => finalizeValidationPackage(paths), /不一致/);
    const hash = createHash('sha256').update('fixture executable').digest('hex').toUpperCase();
    writeFileSync(join(paths.workDirectory, '.validation-health.json'), JSON.stringify({ status: 'passed', version: '1.2.3', executableSha256: hash }), 'utf8');
    finalizeValidationPackage(paths);
    assert.equal(existsSync(paths.workDirectory), false);
    assert.equal(existsSync(join(paths.verificationDirectory, 'electron', 'runner-v1.2.3-portable.exe')), true);
  } finally { rmSync(root, { recursive: true, force: true }); }
});
