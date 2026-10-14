const { mkdirSync, readFileSync, writeFileSync } = require('node:fs');
const { spawnSync } = require('node:child_process');
const { dirname, resolve } = require('node:path');

const root = resolve(__dirname, '..');
const match = readFileSync(resolve(root, 'VERSION.md'), 'utf8').match(/^\s*- 开发提交版本：\s*v(\d+\.\d+\.\d+)\s*$/m);
if (!match) throw new Error('VERSION.md 中未找到格式正确的开发提交版本。');
const validation = process.argv.includes('--validation');
const validationDirectory = `portable-validation-v${match[1].replace(/\./g, '')}`;
const args = ['npm', 'exec', '--allow-git=all', '--yes', '--package=electron-builder@26.0.12', '--', 'electron-builder', '--win', 'portable', '--publish', 'never', `--config.extraMetadata.version=${match[1]}`];
if (validation) {
  const generatedConfig = resolve(root, 'build', `electron-builder-validation-${match[1].replace(/\./g, '')}.json`);
  mkdirSync(dirname(generatedConfig), { recursive: true });
  writeFileSync(generatedConfig, `${JSON.stringify({
    appId: 'local.sequential-task-runner',
    productName: '顺序任务调度器',
    directories: { output: `${validationDirectory}/electron` },
    files: ['frontend-dist/**', 'electron/**', 'package.json', 'VERSION.md'],
    extraResources: [{ from: `${validationDirectory}/backend/sequential-runner-backend.exe`, to: 'backend/sequential-runner-backend.exe' }],
    // 验证包优先保证在受限 CI/桌面会话中可稳定生成；正式发布配置仍保留默认压缩策略。
    compression: 'store',
    win: { target: ['portable'], artifactName: '\${productName}-v\${version}-portable.\${ext}', signAndEditExecutable: false },
  }, null, 2)}\n`, 'utf8');
  args.push('--config', generatedConfig);
}
// 直接执行 Corepack 的 JavaScript 入口，避开 Windows .cmd 与 POSIX shell shim 的派生差异。
const command = process.execPath;
const corepackEntrypoint = resolve(dirname(process.execPath), 'node_modules', 'corepack', 'dist', 'corepack.js');
const cacheRoot = resolve(root, validationDirectory, 'cache');
const env = validation ? { ...process.env, RUNNER_VERSION: match[1], ELECTRON_CACHE: resolve(cacheRoot, 'electron'), ELECTRON_BUILDER_CACHE: resolve(cacheRoot, 'builder') } : { ...process.env, RUNNER_VERSION: match[1] };
const result = spawnSync(command, [corepackEntrypoint, ...args], { cwd: root, env, stdio: 'inherit', shell: false });
if (result.error) throw result.error;
process.exit(result.status ?? 1);
