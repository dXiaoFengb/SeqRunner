const { existsSync, mkdirSync, readFileSync, writeFileSync } = require('node:fs');
const { createHash } = require('node:crypto');
const { dirname, join, resolve } = require('node:path');
const { Cache } = require('@electron/get/dist/cjs/Cache');

function sha256(file) {
  return createHash('sha256').update(readFileSync(file)).digest('hex');
}

function fail(message) {
  throw new Error('Electron 验证缓存准备失败：' + message);
}

function readElectronMetadata(root) {
  const electronDirectory = resolve(root, 'node_modules', 'electron');
  const electronPackage = JSON.parse(readFileSync(join(electronDirectory, 'package.json'), 'utf8'));
  const version = String(electronPackage.version || '').replace(/^v/, '');
  if (!/^\d+\.\d+\.\d+$/.test(version)) fail('node_modules/electron/package.json 的版本无效。');
  const fileName = `electron-v${version}-win32-x64.zip`;
  const checksums = JSON.parse(readFileSync(join(electronDirectory, 'checksums.json'), 'utf8'));
  const expectedSha256 = String(checksums[fileName] || '').toLowerCase();
  if (!/^[a-f0-9]{64}$/.test(expectedSha256)) fail('checksums.json 缺少 Windows x64 Electron ZIP 的 SHA-256。');
  return { version, fileName, expectedSha256 };
}

function prepareValidationElectronCache({ root, cacheRoot, evidencePath, sourceCacheRoot } = {}) {
  if (!root || !cacheRoot || !evidencePath) fail('缺少 root、cacheRoot 或 evidencePath。');
  const metadata = readElectronMetadata(root);
  const downloadUrl = `https://github.com/electron/electron/releases/download/v${metadata.version}/${metadata.fileName}`;
  const cacheKey = Cache.getCacheDirectory(downloadUrl);
  const resolvedSourceCacheRoot = sourceCacheRoot || (process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'electron', 'Cache'));
  if (!resolvedSourceCacheRoot) fail('未找到 LOCALAPPDATA，无法定位本机 Electron 缓存。');
  const sourcePath = join(resolvedSourceCacheRoot, cacheKey, metadata.fileName);
  const targetPath = join(cacheRoot, cacheKey, metadata.fileName);

  if (!existsSync(sourcePath)) fail('本机缓存缺少同版本 ZIP：' + sourcePath);
  const sourceContents = readFileSync(sourcePath);
  const sourceSha256 = createHash('sha256').update(sourceContents).digest('hex');
  if (sourceSha256 !== metadata.expectedSha256) fail('本机缓存 ZIP 的 SHA-256 与 electron/checksums.json 不一致。');
  if (existsSync(targetPath)) fail('隔离缓存目标已存在，不允许覆盖：' + targetPath);
  if (existsSync(evidencePath)) fail('缓存证据文件已存在，不允许覆盖：' + evidencePath);

  mkdirSync(dirname(targetPath), { recursive: true });
  try {
    // Windows 的 EFS 源文件不能可靠地通过 CopyFileEx 跨卷复制；写入已校验的字节，避免继承源文件属性。
    writeFileSync(targetPath, sourceContents);
  } catch (error) {
    fail('无法写入隔离缓存 ZIP：' + error.message);
  }
  const targetSha256 = sha256(targetPath);
  if (targetSha256 !== metadata.expectedSha256) fail('复制后的隔离缓存 ZIP 校验失败。');

  const evidence = {
    schemaVersion: 1,
    source: 'local-electron-cache',
    electronVersion: metadata.version,
    fileName: metadata.fileName,
    downloadUrl,
    cacheKey,
    expectedSha256: metadata.expectedSha256,
    sourcePath,
    sourceSha256,
    targetPath,
    targetSha256,
  };
  mkdirSync(dirname(evidencePath), { recursive: true });
  writeFileSync(evidencePath, JSON.stringify(evidence, null, 2) + '\n', 'utf8');
  return evidence;
}

module.exports = { prepareValidationElectronCache, readElectronMetadata, sha256 };
