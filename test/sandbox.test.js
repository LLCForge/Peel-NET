// Sandbox öz-denetimi: node test/sandbox.test.js
// Örnek kod hiçbir zaman olduğu gibi çalışmaz: çalıştırma kopyasında ağ, dosya, süreç, kayıt defteri çağrıları kayda
// yönlendirilir; motorun kendisi .NET WebAssembly içinde (gerçek dosya sistemi ve P/Invoke yok) ve izin modeliyle
// (--permission: yazma, ağ, alt süreç kapalı) başlatılan alt süreçte çalışır. Updater fixture'ı dosya ve ağ denemesi yapar,
// Loader bellekten derleme yükler.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createNodeSandbox } from '../src/sandbox/node-host.js';
import { decodeFixture } from './fixture-codec.js';

const fixture = (name) => new Uint8Array(decodeFixture(readFileSync(new URL(`./fixtures/${name}.x`, import.meta.url), 'utf8')));
const sb = createNodeSandbox();
try {
  // yan etkiler kaydedilir, yapılmaz: indirilen dizge boş, dosya yok
  const s = await sb.snapshot(fixture('updater-full'));
  assert.equal(s.ok, true, s.error);
  assert.equal(s.mainOk, true);
  const apis = s.fx.map((f) => f.api);
  assert.ok(apis.includes('System.Net.WebClient::DownloadString'));
  assert.ok(apis.includes('System.IO.File::Exists'));
  assert.match(s.out, /Latest: unknown/);
  assert.match(s.out, /Fresh install/);
  // ConfuserEx anti-debug başlatıcısı çalıştırma kopyasında kesilir
  assert.ok(s.neutralized.some((n) => n.startsWith('AntiDebug')));

  // bellekten yükleme: katman yakalanır ve yönlendirilmiş kopyası çalışır
  const l = await sb.snapshot(fixture('loader-layer'));
  assert.equal(l.layers, 1);
  assert.match(l.out, /Hello, Peel!/);
  assert.equal(l.layerBytes[0][0], 0x4d);

  // motor kendi dosyaları dışında okuyamaz (izin modeli): alt süreç çalışma dizinini göremez
  const insp = await sb.inspect(new Uint8Array([0x4d, 0x5a, ...new Array(100).fill(0)]));
  assert.equal(insp.ok, true);
  assert.equal(insp.net, false);

  // bekçi: son tarihi aşan istek sonlandırılır, sonraki istek yeni süreçte çalışır
  const slow = await sb.snapshot(fixture('greeter-plain'), { timeoutMs: 1 });
  assert.equal(slow.ok, false);
  assert.equal(slow.timedOut, true);
  const again = await sb.snapshot(fixture('greeter-plain'));
  assert.equal(again.ok, true);
  assert.match(again.out, /Hello, Peel!/);
  console.log(`sandbox: tamam (yeniden başlatma: ${sb.watchdogStats.restarts})`);
} finally {
  sb.close();
}
