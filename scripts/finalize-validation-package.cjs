const { existsSync, mkdirSync, readFileSync, readdirSync, renameSync } = require('node:fs');
const { createHash } = require('node:crypto');
const { dirname, join, resolve } = require('node:path');
const { assertFinalTargetAvailable, getValidationPackagePaths } = require('./validation-package-paths.cjs');

function sha256(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex').toUpperCase();
}

function finalizeValidationPackage(paths) {
  assertFinalTargetAvailable(paths);
  const markerPath = join(paths.workDirectory, '.validation-health.json');
  if (!existsSync(paths.workDirectory)) throw new Error('验证工作目录不存在，无法最终化。');
  if (!existsSync(markerPath)) throw new Error('缺少通过的隔离健康检查记录，无法最终化。');
  const marker = JSON.parse(readFileSync(markerPath, 'utf8'));
  const executables = readdirSync(paths.applicationDirectory).filter((name) => name.endsWith('-portable.exe'));
  if (executables.length !== 1) throw new Error('验证工作目录中必须恰好有一个 portable EXE。');
  const executablePath = join(paths.applicationDirectory, executables[0]);
  const actualHash = sha256(executablePath);
  if (marker.status !== 'passed' || marker.version !== paths.version || marker.executableSha256 !== actualHash) {
    throw new Error('健康检查记录与当前验证工作包不一致，无法最终化。');
  }
  mkdirSync(dirname(paths.verificationDirectory), { recursive: true });
  renameSync(paths.workDirectory, paths.verificationDirectory);
  const packagedDirectory = join(paths.verificationDirectory, '软件包');
  renameSync(join(packagedDirectory, executables[0]), join(paths.verificationDirectory, executables[0]));
  const evidenceDirectory = join(paths.verificationDirectory, 'Evidence');
  mkdirSync(evidenceDirectory);
  renameSync(packagedDirectory, join(evidenceDirectory, 'package-output'));
  for (const name of ['backend', 'cache', '.validation-health.json', 'electron-cache-preparation.json', 'builder-cache-preparation.json']) {
    const source = join(paths.verificationDirectory, name);
    if (existsSync(source)) renameSync(source, join(evidenceDirectory, name));
  }
  return { verificationDirectory: paths.verificationDirectory, executableSha256: actualHash };
}

if (require.main === module) {
  const result = finalizeValidationPackage(getValidationPackagePaths(resolve(__dirname, '..')));
  process.stdout.write('验证包已最终化：' + result.verificationDirectory + '\nSHA-256：' + result.executableSha256 + '\n');
}

module.exports = { finalizeValidationPackage };
