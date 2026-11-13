// Fixture testleri: test/fixtures/*.x tam hattan (sandbox + davranış doğrulaması + rapor) geçer. Fixture'lar diskte
// XOR + base64'tür (gizlenmiş .NET dosyaları antivirüs sezgilerini tetiklemesin), yalnızca bellekte çözülür.
// Beklentiler aynı adlı .json dosyasındadır (scripts/make-fixtures.js üretir):
//   expect  C# çıktısında bulunmalı · gone  bulunmamalı · detect  tespit kimlikleri · verified  davranış doğrulaması
//   fx      sandbox'ta denenmiş olmalı · layers  yakalanan katman sayısı · protection  ticari koruma raporu (yalnızca tespit)
//   node test/run.js              hepsi
//   node test/run.js greeter-full tek fixture, günlük ve çıktısıyla
import { readdirSync, readFileSync } from 'node:fs';
import { createNodeSandbox } from '../src/sandbox/node-host.js';
import { peel } from '../src/index.js';
import { decodeFixture } from './fixture-codec.js';

const dir = new URL('./fixtures/', import.meta.url);
const filter = process.argv[2];
const files = readdirSync(dir).filter((f) => f.endsWith('.x') && (!filter || f.includes(filter))).sort();

const sb = createNodeSandbox();
let pass = 0;
const t0 = Date.now();
try {
  for (const f of files) {
    const meta = JSON.parse(readFileSync(new URL(f.replace(/\.x$/, '.json'), dir), 'utf8'));
    const bytes = new Uint8Array(decodeFixture(readFileSync(new URL(f, dir), 'utf8')));
    const logs = [];
    const r = await peel(bytes, { sandbox: sb, name: meta.file, timeline: false, onLog: (m, l) => logs.push(`[${l}] ${m}`) });
    const out = r.error ? '' : r.code;
    const fails = [];
    if (r.error) fails.push(`hata: ${r.error}`);
    if (meta.verified && !r.verified) fails.push('davranış doğrulanamadı');
    if (!meta.verified && r.verified && meta.detect.includes('anti-tamper')) fails.push('anti-tamper beklenirken doğrulandı');
    for (const e of meta.expect) if (!out.includes(e)) fails.push(`EXPECT yok: ${e}`);
    for (const g of meta.gone) if (out.includes(g)) fails.push(`GONE duruyor: ${g}`);
    const ids = new Set(r.report?.detection.map((d) => d.id) ?? []);
    for (const d of meta.detect) if (!ids.has(d)) fails.push(`tespit yok: ${d}`);
    const apis = new Set(r.trace?.first?.fx?.map((x) => x.api) ?? []);
    for (const a of meta.fx) if (!apis.has(a)) fails.push(`yan etki kaydı yok: ${a}`);
    if ((r.layers?.length ?? 0) !== meta.layers) fails.push(`katman: ${r.layers?.length ?? 0} != ${meta.layers}`);
    if (meta.protection && !r.report?.protection) fails.push('ticari koruma raporu yok');
    if (meta.protection && r.changed) fails.push('ticari korumalı dosya değiştirildi');
    if (r.events?.some((e) => e.state === 'fail')) fails.push(`geri alınan geçiş: ${r.events.filter((e) => e.state === 'fail').map((e) => `${e.id} (${e.reason})`).join(', ')}`);
    if (!fails.length) pass++;
    console.log(`${fails.length ? 'FAIL' : 'ok  '} ${f} (${r.ms ?? 0} ms)${fails.length ? `\n     ${fails.join('\n     ')}` : ''}`);
    if (filter) {
      console.log(logs.join('\n'));
      console.log(r.report?.summary ?? '');
      if (process.argv.includes('--code')) console.log(out);
    }
  }
} finally {
  sb.close();
}
console.log(`\ntoplam ${Date.now() - t0}ms | ${pass}/${files.length}`);
if (pass !== files.length) process.exitCode = 1;
