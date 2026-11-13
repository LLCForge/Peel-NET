// Node (CLI ve testler) için bekçili sandbox: .NET WebAssembly motoru ayrı, yalıtılmış bir alt süreçte çalışır
// (node --permission; yalnızca motor dosyalarını ve sandbox betiklerini okuma izni; yazma, ağ, alt süreç, worker kapalı).
// Kilitlenen ya da çöken süreç bekçi tarafından öldürülüp yeniden başlatılır.
import { fork } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { existsSync, realpathSync } from 'node:fs';
import path from 'node:path';
import { sandboxApi } from './api.js';
import { createWatchdog } from './watchdog.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '../..');
const PROC = path.join(here, 'node-proc.js');

export function engineDir() {
  const dir = path.join(root, 'public', 'engine', '_framework');
  if (!existsSync(path.join(dir, 'dotnet.js'))) throw new Error('motor derlenmemiş: önce "npm run build:engine" (gerekli: .NET 10 SDK)');
  // izin modeli gerçek yola bakar (junction/symlink)
  return realpathSync(dir);
}

export function createNodeSandbox() {
  const fw = engineDir();
  const dog = createWatchdog({
    spawn() {
      const child = fork(PROC, [fw], {
        execArgv: ['--permission', `--allow-fs-read=${fw}${path.sep}*`, `--allow-fs-read=${here}${path.sep}*`, `--allow-fs-read=${path.join(root, 'package.json')}`],
        stdio: ['ignore', 'ignore', 'pipe', 'ipc'],
        serialization: 'advanced',
      });
      let onErr = () => {};
      let stderr = '';
      child.stderr.on('data', (d) => { stderr = (stderr + d).slice(-2000); });
      child.on('error', (e) => onErr(e?.message ?? String(e)));
      child.on('exit', (code) => { if (code !== 0 && code !== null) onErr(`sandbox süreci çıktı (${code}): ${stderr.trim().split('\n').find((l) => l.includes('Assertion') || l.includes('Error')) ?? stderr.trim().split('\n').pop() ?? ''}`); });
      return {
        post: (m) => child.send(m),
        onMessage: (cb) => child.on('message', (m) => { if (!m?.ready) cb(m); }),
        onError: (cb) => { onErr = cb; },
        terminate: () => { onErr = () => {}; child.kill(); },
        ref: () => { child.ref(); child.channel?.ref(); },
        unref: () => { child.unref(); child.channel?.unref(); },
      };
    },
  });
  return { ...sandboxApi(dog.send), watchdogStats: dog.stats, kill: (reason) => dog.kill(reason), close: () => dog.terminate() };
}
