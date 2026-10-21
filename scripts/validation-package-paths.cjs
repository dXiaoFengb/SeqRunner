const { existsSync, readFileSync } = require('node:fs');
const { join, resolve } = require('node:path');

function readDevelopmentVersion(root) {
  const match = readFileSync(resolve(root, 'VERSION.md'), 'utf8')
    .match(/^\s*- 开发提交版本：\s*v(\d+\.\d+\.\d+)\s*$/m);
  if (!match) throw new Error('VERSION.md 中未找到格式正确的开发提交版本。');
  return match[1];
}

function assertLegacySuffixIsUnused(env = process.env) {
  if (env.RUNNER_VALIDATION_SUFFIX) {
    throw new Error('验证包已改用开发版本目录，不再支持 RUNNER_VALIDATION_SUFFIX。');
  }
}

function getValidationPackagePaths(root) {
  assertLegacySuffixIsUnused();
  const version = readDevelopmentVersion(root);
  const workDirectory = resolve(root, '产物', '工作', 'v' + version);
  const verificationDirectory = resolve(root, '产物', '验证', 'v' + version);
  return {
    root,
    version,
    workDirectory,
    verificationDirectory,
    backendDirectory: resolve(workDirectory, 'backend'),
    electronDirectory: resolve(workDirectory, 'electron'),
    cacheDirectory: resolve(workDirectory, 'cache'),
  };
}

function assertPreparationTargetsAvailable(paths) {
  if (existsSync(paths.workDirectory)) throw new Error('验证工作目录已存在，不允许覆盖：' + paths.workDirectory);
  if (existsSync(paths.verificationDirectory)) throw new Error('验证包目录已存在，不允许覆盖：' + paths.verificationDirectory);
}

function assertFinalTargetAvailable(paths) {
  if (existsSync(paths.verificationDirectory)) throw new Error('验证包目录已存在，不允许覆盖：' + paths.verificationDirectory);
}

function assertPortableValidationReady(paths, env = process.env) {
  if (env.RUNNER_VALIDATION_PREPARED !== '1') {
    throw new Error('验证 portable 打包只能由 package:validation:prepare 的准备阶段调用。');
  }
  if (!existsSync(paths.workDirectory)) throw new Error('验证工作目录不存在，无法打包 portable EXE。');
  assertFinalTargetAvailable(paths);
  if (!existsSync(join(paths.backendDirectory, 'sequential-runner-backend.exe'))) {
    throw new Error('验证后端资源不存在，无法打包 portable EXE。');
  }
  if (existsSync(paths.electronDirectory)) {
    throw new Error('验证 Electron 输出目录已存在，不允许覆盖：' + paths.electronDirectory);
  }
}

module.exports = {
  assertFinalTargetAvailable,
  assertLegacySuffixIsUnused,
  assertPortableValidationReady,
  assertPreparationTargetsAvailable,
  getValidationPackagePaths,
  readDevelopmentVersion,
};
