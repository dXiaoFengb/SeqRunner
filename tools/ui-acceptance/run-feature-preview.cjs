const fs = require('node:fs');
const path = require('node:path');
const { _electron: electron } = require('playwright');

function option(name) {
  const index = process.argv.indexOf(name);
  if (index >= 0) return process.argv[index + 1];
  const prefix = `${name}=`;
  return process.argv.find((value) => value.startsWith(prefix))?.slice(prefix.length);
}

function writeJson(file, value) {
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, 'utf8');
}

async function main() {
  const root = option('--root');
  const previewDirectory = option('--preview-directory');
  const viteUrl = option('--vite-url');
  const autoClose = process.argv.includes('--auto-close');
  if (!root || !previewDirectory || !viteUrl) throw new Error('缺少预览根目录、运行目录或 Vite 地址。');

  const evidence = path.join(previewDirectory, '在线验收.json');
  const screenshot = path.join(previewDirectory, '在线验收截图.png');
  const errorLog = path.join(previewDirectory, 'Playwright错误.log');
  const executablePath = path.join(root, 'node_modules', 'electron', 'dist', 'electron.exe');
  if (!fs.existsSync(executablePath)) throw new Error(`项目 Electron 不存在：${executablePath}`);

  let application;
  const startedAt = new Date().toISOString();
  try {
    application = await electron.launch({ executablePath, args: ['.'], env: { ...process.env, VITE_DEV_SERVER_URL: viteUrl } });
    const page = await application.firstWindow({ timeout: 30000 });
    await page.getByText('脚本管理', { exact: true }).waitFor({ state: 'visible', timeout: 30000 });
    await page.getByText(/API 在线/).waitFor({ state: 'visible', timeout: 30000 });
    await page.getByText('实时已连接', { exact: true }).waitFor({ state: 'visible', timeout: 30000 });
    await page.screenshot({ path: screenshot });
    if (process.env.RUNNER_PREVIEW_QQ_CAPTURE === '1') {
      const fixtureImage = await require('qrcode').toDataURL('isolated-qq-fixture');
      await application.evaluate(({ ipcMain }, fixtureImage) => {
        const handlers = {
          'runner:qq-status': () => ({ bound:false, enabled:false, state:'unbound' }),
          'runner:qq-cancel': () => ({ bound:false, enabled:false, state:'unbound' }),
          'runner:qq-start': () => ({ id:'fixture', image:fixtureImage }),
          'runner:qq-poll': () => ({ state:'waiting' }),
        };
        for (const [name, handler] of Object.entries(handlers)) { ipcMain.removeHandler(name); ipcMain.handle(name, handler); }
      }, fixtureImage);
      await page.getByRole('button', { name:'设置', exact:true }).click();
      await page.getByRole('tab', { name:'通知设置' }).click();
      const layouts = [];
      for (const width of [1280, 960, 640]) {
        await application.evaluate(({ BrowserWindow }, size) => { BrowserWindow.getAllWindows()[0].setSize(size, 900); }, width);
        await page.locator('[data-qq-channel]').click();
        const dialog = page.getByRole('dialog', { name:'QQ 官方机器人通知' });
        await dialog.waitFor();
        await page.screenshot({ path:path.join(previewDirectory, `QQ配置-${width}.png`) });
        await dialog.getByRole('button', { name:'扫码绑定' }).click();
        await page.getByAltText('手机 QQ 扫码绑定二维码').waitFor();
        await page.screenshot({ path:path.join(previewDirectory, `QQ扫码-${width}.png`) });
        const dimensions = await page.evaluate(() => ({ width:document.documentElement.clientWidth, scrollWidth:document.documentElement.scrollWidth, dialog:document.querySelector('.qq-dialog').getBoundingClientRect().toJSON() }));
        if (dimensions.scrollWidth > dimensions.width || dimensions.dialog.left < 0 || dimensions.dialog.right > dimensions.width || dimensions.dialog.top < 0 || dimensions.dialog.bottom > 900) throw new Error(`QQ 弹窗在 ${width}px 存在溢出。`);
        layouts.push({ requestedWidth:width, ...dimensions });
        await page.getByRole('button', { name:'关闭 QQ 配置' }).click();
      }
      writeJson(path.join(previewDirectory, 'QQ弹窗布局.json'), layouts);
    }
    writeJson(evidence, {
      status: 'online',
      startedAt,
      checkedAt: new Date().toISOString(),
      viteUrl,
      electronProcessId: application.process().pid,
      checks: ['主界面已加载', 'API 在线', '实时已连接'],
      screenshot: path.basename(screenshot),
    });
    const closed = new Promise((resolve) => application.on('close', resolve));
    if (autoClose) await application.close();
    await closed;
    writeJson(evidence, {
      ...JSON.parse(fs.readFileSync(evidence, 'utf8')),
      status: 'closed',
      closedAt: new Date().toISOString(),
    });
  } catch (error) {
    const message = error instanceof Error ? `${error.stack || error.message}\n` : `${String(error)}\n`;
    fs.writeFileSync(errorLog, message, 'utf8');
    writeJson(evidence, { status: 'failed', startedAt, failedAt: new Date().toISOString(), viteUrl, error: error instanceof Error ? error.message : String(error) });
    if (application) await application.close().catch(() => undefined);
    throw error;
  }
}

main().catch((error) => {
  process.stderr.write(`预览在线验收失败：${error instanceof Error ? error.message : String(error)}\n`);
  process.exitCode = 1;
});
