// Motor ileti işleyicisi (Web Worker ve Node alt süreci ortak). .NET WebAssembly çalışma zamanı ilk istekte yüklenir;
// çağrılar durumsuzdur (her iletide modül baytları gelir), bekçi süreci öldürse bile hat kaldığı yerden sürer.
// İleti türleri: inspect | snapshot | pass | decompile | version

/** @param {() => Promise<object>} load PeelNet.Engine'in dışa açtığı Peel.Api nesnesini döndürür */
export function createEngine(load) {
  let api = null;
  const bytesOf = (v) => (v instanceof Uint8Array ? v : Uint8Array.from(v ?? []));
  const parse = (json) => {
    const r = JSON.parse(json);
    if (r.ok === false && r.error) return { ok: false, error: r.error };
    return { ok: true, ...r };
  };
  return {
    async handle(msg) {
      api ??= await load();
      switch (msg.type) {
        case 'load':
          return { ok: true, version: api.Version() };
        case 'inspect':
          return parse(api.Inspect(msg.bytes));
        case 'snapshot': {
          const r = parse(api.Snapshot(msg.bytes, Boolean(msg.main), Boolean(msg.probes)));
          if (r.ok && r.layers > 0) {
            r.layerBytes = [];
            for (let i = 0; i < r.layers; i++) r.layerBytes.push(bytesOf(api.TakeLayer(i)));
          }
          return r;
        }
        case 'pass': {
          const r = parse(api.Pass(msg.bytes, msg.pass, JSON.stringify(msg.options ?? {})));
          if (r.ok && r.changed) r.bytes = bytesOf(api.TakeOutput());
          return r;
        }
        case 'decompile':
          return parse(api.Decompile(msg.bytes, msg.mode === 'il' ? 'il' : 'cs', String(msg.name ?? 'module.dll')));
        default:
          return { ok: false, error: `bilinmeyen ileti: ${msg.type}` };
      }
    },
  };
}

/** dotnet.js modülünden çalışma zamanını başlatır ve dışa açılan API'yi döndürür */
export async function startRuntime(dotnetModule) {
  const { dotnet } = dotnetModule;
  // örnek kodun Console çıktısı motor içinde yakalanır; çalışma zamanının kendi iletileri bastırılmaz (hata ayıklama için)
  const rt = await dotnet.create();
  const exports = await rt.getAssemblyExports('PeelNet.Engine.dll');
  return exports.Peel.Api;
}
