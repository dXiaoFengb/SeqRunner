const fs = require('node:fs');
const path = require('node:path');

const legacyEntries = ['config.json', 'history.json', 'backups', 'detail_logs'];
const portableDataDirectoryName = 'UserData';
const legacyDataDirectoryNames = ['用户数据', 'data'];

function dataDirectory({ packaged, sourceRoot, execPath, environment = process.env }) {
  if (!packaged) return path.join(sourceRoot, 'data');
  return environment.RUNNER_USER_DATA_DIR || path.join(environment.PORTABLE_EXECUTABLE_DIR || path.dirname(execPath), portableDataDirectoryName);
}

function prepareDataDirectory(options) {
  const target = dataDirectory(options);
  const root = options.environment?.PORTABLE_EXECUTABLE_DIR || path.dirname(options.execPath);
  const legacy = legacyDataDirectoryNames.map((name) => path.join(root, name)).find((candidate) => fs.existsSync(candidate));
  const migrate = options.packaged && !fs.existsSync(target) && legacy;
  fs.mkdirSync(target, { recursive: true });
  if (migrate) {
    for (const name of legacyEntries) {
      const source = path.join(legacy, name);
      if (fs.existsSync(source)) fs.cpSync(source, path.join(target, name), { recursive: true, errorOnExist: true });
    }
  }
  return target;
}

module.exports = { dataDirectory, legacyEntries, legacyDataDirectoryNames, portableDataDirectoryName, prepareDataDirectory };
