const { mkdirSync, readFileSync, writeFileSync } = require('node:fs');
const { spawnSync } = require('node:child_process');
const { dirname, relative, resolve } = require('node:path');
const {
  assertPortableValidationReady,
  getValidationPackagePaths,
  readDevelopmentVersion,
} = require('./validation-package-paths.cjs');

const root = resolve(__dirname, '..');
const version = readDevelopmentVersion(root);
const validation = process.argv.includes('--validation');
const args = ['npm', 'exec', '--allow-git=all', '--yes', '--package=electron-builder@26.0.12', '--', 'electron-builder', '--win', 'portable', '--publish', 'never', '--config.extraMetadata.version=' + version];
let paths = null;
if (validation) {
  paths = getValidationPackagePaths(root);
  assertPortableValidationReady(paths);
  const generatedConfig = resolve(root, 'build', 'electron-builder-validation-v' + paths.version + '.json');
  mkdirSync(dirname(generatedConfig), { recursive: true });
  const baseConfig = JSON.parse(readFileSync(resolve(root, 'electron-builder.validation.json'), 'utf8'));
  writeFileSync(generatedConfig, JSON.stringify({
    ...baseConfig,
    directories: { output: relative(root, paths.electronDirectory) },
    extraResources: [{ from: relative(root, resolve(paths.backendDirectory, 'sequential-runner-backend.exe')), to: 'backend/sequential-runner-backend.exe' }],
    // 验证包优先保证在受限 CI/桌面会话中可稳定生成；正式发布配置仍保留默认压缩策略。
    compression: 'store',
  }, null, 2) + '\n', 'utf8');
  args.push('--config', generatedConfig);
}
// 直接执行 Corepack 的 JavaScript 入口，避开 Windows .cmd 与 POSIX shell shim 的派生差异。
const command = process.execPath;
const corepackEntrypoint = resolve(dirname(process.execPath), 'node_modules', 'corepack', 'dist', 'corepack.js');
const cacheRoot = paths && paths.cacheDirectory;
const env = validation ? { ...process.env, RUNNER_VERSION: version, ELECTRON_CACHE: resolve(cacheRoot, 'electron'), ELECTRON_BUILDER_CACHE: resolve(cacheRoot, 'builder') } : { ...process.env, RUNNER_VERSION: version };
const result = spawnSync(command, [corepackEntrypoint, ...args], { cwd: root, env, stdio: 'inherit', shell: false });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
