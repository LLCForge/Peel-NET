// Yalıtılmış alt süreç: node --permission ile başlatılır (ağ, alt süreç, worker, dosya yazma kapalı; okuma yalnızca motor
// dosyaları). .NET WebAssembly çalışma zamanı burada çalışır; örnek kod onun içinde, yan etkileri Peel.Fx'e yönlendirilmiş
// bir kopya olarak yürütülür. Çalışma zamanı iddia hatasıyla çökerse süreç çıkar, bekçi yenisini başlatır.
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { createEngine, startRuntime } from './engine.js';

const fw = process.argv[2];
const engine = createEngine(async () => startRuntime(await import(pathToFileURL(path.join(fw, 'dotnet.js')).href)));

process.on('message', async ({ id, ...msg }) => {
  let result;
  try {
    result = await engine.handle(msg);
  } catch (err) {
    result = { ok: false, error: String(err?.message ?? err).split('\n').slice(0, 3).join(' | ') };
  }
  process.send({ id, ...result });
});
process.send({ ready: true });
