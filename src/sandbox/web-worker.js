// Web Worker tarafı: .NET WebAssembly motoru (public/engine/_framework, dotnet.js) burada çalışır; sonsuz döngüde ya da
// çalışma zamanı çöktüğünde ana thread'deki bekçi worker'ı sonlandırır.
//
// Yalıtım worker düzeyindedir:
//  1. CSP: worker betiğinin yanıt başlığı (dev/preview: vite.config.js ara katmanı, üretim: public/_headers)
//     default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'
//  2. JS kilidi: motor dosyaları yüklendikten sonra ağ API'leri, eval/Function kurucuları, dizge zamanlayıcıları ve iç
//     içe worker kaldırılır. Başlık veremeyen barındırmada tek JS katmanı budur; durum ana thread'e 'env' iletisiyle bildirilir.
//  3. Örnek kodun kendisi .NET içinde, yan etkileri kayda yönlendirilmiş bir kopya olarak çalışır (engine/Sandbox.cs).
import { createEngine, startRuntime } from './engine.js';

// CSP sınaması: bu betiğin kendi yanıtı yeniden (aynı köken) okunur ve başlıkta connect-src 'self' aranır
const cspProbe = fetch(self.location.href, { method: 'HEAD', cache: 'no-store' })
  .then((r) => /(^|;)\s*connect-src 'self'\s*(;|$)/.test(r.headers.get('content-security-policy') ?? ''), () => false);

// Kaldırılan genel adlar: ağ, kod üretme, iç içe worker, kalıcı depolama
const BLOCKED = [
  'fetch', 'fetchLater', 'XMLHttpRequest', 'WebSocket', 'WebSocketStream', 'EventSource', 'WebTransport',
  'importScripts', 'Worker', 'SharedWorker', 'caches', 'BroadcastChannel', 'indexedDB',
];
const denied = () => { throw new EvalError('Peel sandbox: kod üretimi kapalı'); };

function lockdown() {
  const ctorProtos = [
    Function.prototype,
    Object.getPrototypeOf(async function () {}),
    Object.getPrototypeOf(function* () {}),
    Object.getPrototypeOf(async function* () {}),
  ];
  for (const name of BLOCKED) {
    for (let o = self; o; o = Object.getPrototypeOf(o)) {
      if (Object.prototype.hasOwnProperty.call(o, name)) {
        Object.defineProperty(o, name, { value: undefined, writable: false, configurable: false });
      }
    }
  }
  const BlockedFunction = function Function() { denied(); };
  BlockedFunction.prototype = Function.prototype;
  const freeze = (o, k, value) => Object.defineProperty(o, k, { value, writable: false, configurable: false });
  freeze(self, 'eval', denied);
  freeze(self, 'Function', BlockedFunction);
  for (const proto of ctorProtos) freeze(proto, 'constructor', BlockedFunction);
  for (const name of ['setTimeout', 'setInterval']) {
    const real = self[name];
    Object.defineProperty(self, name, {
      value: (fn, ...rest) => (typeof fn === 'function' ? real(fn, ...rest) : 0),
      writable: false,
      configurable: false,
    });
  }
}

let base = null;
// motor dosyaları (dotnet.js, wasm, derlemeler) çalışma zamanı oluşturulurken bir kez alınır; ardından kilit kurulur
const engine = createEngine(async () => {
  if (!base) throw new Error('motor adresi bilinmiyor');
  const api = await startRuntime(await import(/* @vite-ignore */ `${base}dotnet.js`));
  const csp = await cspProbe;
  lockdown();
  self.postMessage({ type: 'env', csp, locked: true });
  return api;
});

self.addEventListener('message', async ({ data }) => {
  const { id, engineBase, ...msg } = data ?? {};
  // motor adresi yalnızca aynı kökenden kabul edilir
  if (engineBase && !base && new URL(engineBase, self.location.href).origin === self.location.origin) base = new URL(engineBase, self.location.href).href;
  let result;
  try {
    result = await engine.handle(msg);
  } catch (err) {
    result = { ok: false, error: String(err?.message ?? err).split('\n').filter(Boolean).slice(-3).join(' | ') };
  }
  const transfer = [result.bytes?.buffer, ...(result.layerBytes ?? []).map((b) => b.buffer)].filter(Boolean);
  self.postMessage({ id, ...result }, transfer);
});
