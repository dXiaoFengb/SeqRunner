const { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync } = require('node:fs');
const { createHash } = require('node:crypto');
const { dirname, join, relative, resolve } = require('node:path');

const NSIS_CACHE_DIRECTORY = '817365875';
const EXPECTED_NSIS_MANIFEST_SHA256 = 'e56ddba6bda7ee3619b9183fbeaa6481a2f4c71919875f22c951a79808f4fae1';

function fail(message) {
  throw new Error('electron-builder 验证缓存准备失败：' + message);
}

function sha256(contents) {
  return createHash('sha256').update(contents).digest('hex');
}

function buildFileManifest(directory) {
  const records = [];
  function visit(current) {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const fullPath = join(current, entry.name);
      if (entry.isDirectory()) {
        visit(fullPath);
        continue;
      }
      if (!entry.isFile()) fail('缓存源包含非普通文件：' + fullPath);
      const contents = readFileSync(fullPath);
      records.push(`${relative(directory, fullPath).replace(/\\/g, '/')}` + `\t${contents.length}\t${sha256(contents)}`);
    }
  }
  visit(directory);
  records.sort();
  return {
    fileCount: records.length,
    byteCount: records.reduce((total, record) => total + Number(record.split('\t')[1]), 0),
    sha256: sha256(Buffer.from(records.join('\n'), 'utf8')),
  };
}

function copyDirectoryByBytes(sourceDirectory, targetDirectory) {
  function visit(source, target) {
    mkdirSync(target, { recursive: true });
    for (const entry of readdirSync(source, { withFileTypes: true })) {
      const sourcePath = join(source, entry.name);
      const targetPath = join(target, entry.name);
      if (entry.isDirectory()) {
        visit(sourcePath, targetPath);
        continue;
      }
      if (!entry.isFile()) fail('缓存源包含非普通文件：' + sourcePath);
      writeFileSync(targetPath, readFileSync(sourcePath));
    }
  }
  visit(sourceDirectory, targetDirectory);
}

function prepareValidationBuilderCache({ cacheRoot, evidencePath, sourceCacheRoot, expectedManifestSha256 = EXPECTED_NSIS_MANIFEST_SHA256 } = {}) {
  if (!cacheRoot || !evidencePath) fail('缺少 cacheRoot 或 evidencePath。');
  const resolvedSourceCacheRoot = sourceCacheRoot || (process.env.LOCALAPPDATA && join(process.env.LOCALAPPDATA, 'electron-builder', 'Cache'));
  if (!resolvedSourceCacheRoot) fail('未找到 LOCALAPPDATA，无法定位本机 electron-builder 缓存。');
  const sourceDirectory = join(resolvedSourceCacheRoot, 'nsis', NSIS_CACHE_DIRECTORY);
  const targetDirectory = join(resolve(cacheRoot), 'nsis', NSIS_CACHE_DIRECTORY);

  if (!existsSync(sourceDirectory)) fail('本机缓存缺少 NSIS 目录：' + sourceDirectory);
  if (existsSync(targetDirectory)) fail('隔离缓存目标已存在，不允许覆盖：' + targetDirectory);
  if (existsSync(evidencePath)) fail('缓存证据文件已存在，不允许覆盖：' + evidencePath);

  const sourceManifest = buildFileManifest(sourceDirectory);
  if (sourceManifest.sha256 !== String(expectedManifestSha256).toLowerCase()) fail('本机 NSIS 缓存文件清单与受信指纹不一致。');

  try {
    copyDirectoryByBytes(sourceDirectory, targetDirectory);
  } catch (error) {
    fail('无法写入隔离 NSIS 缓存：' + error.message);
  }
  const targetManifest = buildFileManifest(targetDirectory);
  if (targetManifest.sha256 !== sourceManifest.sha256 || targetManifest.fileCount !== sourceManifest.fileCount || targetManifest.byteCount !== sourceManifest.byteCount) {
    fail('复制后的隔离 NSIS 缓存校验失败。');
  }

  const evidence = {
    schemaVersion: 1,
    source: 'local-electron-builder-cache',
    artifact: 'nsis-3.0.4.1',
    nsisVersion: '3.0.4.1',
    expectedManifestSha256: String(expectedManifestSha256).toLowerCase(),
    sourceDirectory,
    targetDirectory,
    sourceManifest,
    targetManifest,
  };
  mkdirSync(dirname(evidencePath), { recursive: true });
  writeFileSync(evidencePath, JSON.stringify(evidence, null, 2) + '\n', 'utf8');
  return evidence;
}

module.exports = { EXPECTED_NSIS_MANIFEST_SHA256, NSIS_CACHE_DIRECTORY, buildFileManifest, prepareValidationBuilderCache };
