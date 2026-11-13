// Tarayıcı için bekçili sandbox: .NET WebAssembly motoru ayrı bir Web Worker'dadır. Son tarihi aşan worker (sonsuz döngü)
// sonlandırılır, sıradaki istek yeni worker'da başlar. node-host.js'in tarayıcı karşılığı.
import { sandboxApi } from './api.js';
import { createWatchdog } from './watchdog.js';

export function createWebSandbox() {
  // worker'ın bildirdiği yalıtım durumu: csp = worker yanıtında CSP başlığı etkin, locked = JS kilidi kuruldu
  const security = { csp: null, locked: false };
  const listeners = new Set();

  const spawn = () => {
    const w = new Worker(new URL('./web-worker.js', import.meta.url), { type: 'module', name: 'peel-net' });
    let onErr = () => {};
    let onMsg = () => {};
    w.addEventListener('error', (e) => { e.preventDefault(); onErr(e.message || 'Sandbox worker yüklenemedi'); });
    w.addEventListener('messageerror', () => onErr('Sandbox mesajı çözülemedi'));
    w.addEventListener('message', ({ data }) => {
      if (data?.type === 'env') {
        // worker kullanıcı kodundan önce bildirir; bir kez CSP'siz görülen durum zayıf sayılır
        security.csp = security.csp === false ? false : data.csp === true;
        security.locked = data.locked === true;
        for (const fn of listeners) fn({ ...security });
        return;
      }
      onMsg(data);
    });
    return {
      post: (m) => w.postMessage(m),
      onMessage: (cb) => { onMsg = cb; },
      onError: (cb) => { onErr = cb; },
      terminate: () => { onErr = () => {}; w.terminate(); },
    };
  };

  // motor dosyaları public/engine/_framework altındadır (Vite bunları paketlemez, olduğu gibi kopyalar)
  const engineBase = new URL(`${import.meta.env?.BASE_URL ?? '/'}engine/_framework/`, location.href).href;
  const dog = createWatchdog({ spawn, prepare: (m) => ({ ...m, engineBase }) });
  return {
    ...sandboxApi(dog.send),
    security: () => ({ ...security }),
    onSecurity: (fn) => { listeners.add(fn); return () => listeners.delete(fn); },
    watchdogStats: dog.stats,
    // Durdur: uçuştaki ve kuyruktaki işler hata alır, worker sonraki istekte yeniden başlar
    kill: (reason) => dog.kill(reason),
    close: () => dog.terminate(),
  };
}
