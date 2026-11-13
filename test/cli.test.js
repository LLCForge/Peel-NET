// CLI testleri: node test/cli.test.js
// Gizlenmiş örnekler diske ham yazılmaz: kodlu fixture bellekte çözülüp stdin'den verilir. Dizin girdisi için yalnızca
// gizlenmemiş örnek (greeter-plain) geçici dizine yazılır.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, rmSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { decodeFixture } from './fixture-codec.js';

const BIN = fileURLToPath(new URL('../bin/peel-net.js', import.meta.url));
const cli = (args, input) => spawnSync(process.execPath, [BIN, ...args], { input, timeout: 300_000 });
const fixture = (name) => decodeFixture(readFileSync(new URL(`./fixtures/${name}.x`, import.meta.url), 'utf8'));

const tmp = mkdtempSync(join(tmpdir(), 'peel-net-cli-'));
try {
  // kullanım ve hata çıkış kodları
  assert.equal(cli(['-h']).status, 0);
  assert.match(cli(['--help', '--lang', 'en']).stdout.toString(), /Exit codes/);
  assert.equal(cli([]).status, 2);
  assert.equal(cli(['x.exe', '--lang', 'de']).status, 2);
  assert.equal(cli(['x.exe', '--fail-on', 'none']).status, 2);
  assert.equal(cli(['x.exe', '--only', 'strings,nope']).status, 2);
  assert.equal(cli(['x.exe', '--bogus']).status, 2);
  assert.equal(cli([join(tmp, 'yok.exe'), '-q']).status, 1);

  // .NET olmayan girdi: hata (1), çökme yok
  let r = cli(['-', '-q'], Buffer.from('MZ bu bir derleme değil'.padEnd(200, '.')));
  assert.equal(r.status, 1);

  // stdin: C# stdout'a, temizlenmiş derleme -a ile, metin rapor -r ile
  const asm = join(tmp, 'clean.exe');
  const repTxt = join(tmp, 'rep.txt');
  r = cli(['-', '-q', '-a', asm, '-r', repTxt, '--lang', 'en'], fixture('greeter-full'));
  assert.equal(r.status, 0, r.stderr.toString());
  const cs = r.stdout.toString();
  assert.ok(cs.includes('Console.WriteLine("Hello, " + text + "!");'), cs.slice(0, 400));
  assert.ok(cs.includes('"Update channel: https://updates.example.com/greeter/stable"'));
  assert.ok(readFileSync(asm).subarray(0, 2).toString() === 'MZ');
  const txt = readFileSync(repTxt, 'utf8');
  assert.match(txt, /ConfuserEx/);
  assert.match(txt, /verified: yes/);

  // JSON rapor stdout'a; --il ile IL listesi
  r = cli(['-', '--json', '-q'], fixture('updater-full'));
  assert.equal(r.status, 0);
  const rep = JSON.parse(r.stdout.toString());
  assert.equal(rep.ok, true);
  assert.ok(rep.report.iocs.urls.includes('https://updates.peel-demo.invalid/channel/stable.txt'));
  assert.ok(rep.report.behaviour.network.some((n) => n.api === 'System.Net.WebClient::DownloadString'));
  r = cli(['-', '--il', '-q', '--only', 'strings'], fixture('greeter-constants'));
  assert.equal(r.status, 0);
  assert.match(r.stdout.toString(), /ldstr "Hello, "/);

  // ticari koruma: dosya değiştirilmez, raporda koruma; --fail-on eşiği (çıkış 4)
  r = cli(['-', '--json', '-q'], fixture('markers-commercial'));
  const prot = JSON.parse(r.stdout.toString());
  assert.equal(prot.changed, false);
  assert.ok(prot.report.protection);
  r = cli(['-', '-q', '--fail-on', 'low'], fixture('loader-layer'));
  assert.equal(r.status, 4);

  // katman yakalama: --layers dizinine yazılır
  const layers = join(tmp, 'layers');
  r = cli(['-', '-q', '--layers', layers, '-o', join(tmp, 'loader.cs')], fixture('loader-layer'));
  assert.equal(r.status, 0);
  assert.equal(readdirSync(layers).length, 1);
  assert.equal(readFileSync(join(layers, readdirSync(layers)[0])).subarray(0, 2).toString(), 'MZ');

  // dizin: yalnızca .exe/.dll, yapı korunur
  const src = join(tmp, 'src');
  mkdirSync(join(src, 'a'), { recursive: true });
  writeFileSync(join(src, 'a', 'Greeter.exe'), fixture('greeter-plain'));
  writeFileSync(join(src, 'readme.txt'), 'not .net');
  const out = join(tmp, 'out');
  const repJson = join(tmp, 'rep.json');
  r = cli([src, '-d', out, '-r', repJson, '-q']);
  assert.equal(r.status, 0, r.stderr.toString());
  assert.ok(existsSync(join(out, 'a', 'Greeter.cs')));
  assert.ok(!existsSync(join(out, 'readme.cs')));
  assert.deepEqual(JSON.parse(readFileSync(repJson, 'utf8')).map((e) => e.file), ['a/Greeter.exe']);
  assert.equal(cli([src, '-o', join(tmp, 'x.cs')]).status, 2);
  console.log('cli: tamam');
} finally {
  rmSync(tmp, { recursive: true, force: true });
}
