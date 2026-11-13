// Fixture üretimi: node scripts/make-fixtures.js
// samples/ altındaki zararsız örnekler derlenir (.NET SDK + NuGet başvuru derlemeleri), açık kaynak gizleyicilerle
// (ConfuserEx 2 = Confuser.MSBuild 1.6.0, Obfuscar 2.2.50; ikisi de MIT) gizlenir ve test/fixtures/<ad>.x olarak
// XOR + base64 kodlanıp yazılır (test/fixture-codec.js). Gizlenmiş .NET dosyaları antivirüs sezgilerini tetikleyebilir;
// ham hâlleri yalnızca geçici dizinde kalır. Beklentiler test/fixtures/<ad>.json dosyalarındadır.
// Yalnızca Windows: ConfuserEx motoru .NET Framework 4.8 üzerinde çalışır.
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { encodeFixture } from '../test/fixture-codec.js';

const root = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
const work = path.join(tmpdir(), 'peel-net-fixtures');
const fixtures = path.join(root, 'test', 'fixtures');
const only = process.argv[2];

function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', ...opts });
  if (r.status !== 0) {
    console.error(`${cmd} ${args.join(' ')}\n${r.stdout}\n${r.stderr}`);
    throw new Error(`${path.basename(cmd)} başarısız (${r.status})`);
  }
  return r.stdout;
}

const build = (proj, out) => run('dotnet', ['build', proj, '-c', 'Release', '-nologo', '-v', 'q', '-o', out]);

rmSync(work, { recursive: true, force: true });
mkdirSync(work, { recursive: true });
cpSync(path.join(root, 'samples'), path.join(work, 'src'), { recursive: true });
const bin = (n) => path.join(work, 'bin', n);

console.log('örnekler derleniyor ...');
for (const p of ['Greeter', 'Updater', 'MathKit', 'Markers']) build(path.join(work, 'src', p, `${p}.csproj`), bin(p));
// Loader: Greeter'i 0x5A ile XOR'lanmış gömülü kaynak olarak taşır
const greeter = readFileSync(path.join(bin('Greeter'), 'Greeter.exe'));
writeFileSync(path.join(work, 'src', 'Loader', 'payload.bin'), Buffer.from(greeter.map((b) => b ^ 0x5a)));
build(path.join(work, 'src', 'Loader', 'Loader.csproj'), bin('Loader'));

console.log('araçlar derleniyor (ConfuserEx 2, Obfuscar) ...');
const tools = path.join(work, 'tools');
build(path.join(root, 'scripts', 'fixtures', 'CrRunner', 'CrRunner.csproj'), tools);
const crRunner = path.join(tools, 'CrRunner.exe');
const obfuscar = readFileSync(path.join(tools, 'obfuscar.path'), 'utf8').trim();

const confuse = (name, input, protections) => {
  const dir = path.join(work, 'cx', name);
  mkdirSync(dir, { recursive: true });
  const rules = protections.map(([id, args = {}]) =>
    `    <protection id="${id}">${Object.entries(args).map(([k, v]) => `<argument name="${k}" value="${v}" />`).join('')}</protection>`).join('\n');
  writeFileSync(path.join(dir, 'p.crproj'), `<project outputDir="${path.join(dir, 'out')}" baseDir="${path.dirname(input)}" xmlns="http://confuser.codeplex.com">
  <rule pattern="true" inherit="false">
${rules}
  </rule>
  <module path="${input}" />
</project>
`);
  run(crRunner, [path.join(dir, 'p.crproj')]);
  return readFileSync(path.join(dir, 'out', path.basename(input)));
};

const obfuscate = (name, input) => {
  const dir = path.join(work, 'obf', name);
  mkdirSync(dir, { recursive: true });
  writeFileSync(path.join(dir, 'obfuscar.xml'), `<?xml version="1.0"?>
<Obfuscator>
  <Var name="InPath" value="${path.dirname(input)}" />
  <Var name="OutPath" value="${path.join(dir, 'out')}" />
  <Var name="KeepPublicApi" value="false" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="RenameProperties" value="true" />
  <Var name="RenameEvents" value="true" />
  <Var name="RenameFields" value="true" />
  <Module file="$(InPath)\\${path.basename(input)}" />
</Obfuscator>
`);
  run(obfuscar, [path.join(dir, 'obfuscar.xml')], { cwd: dir });
  return readFileSync(path.join(dir, 'out', path.basename(input)));
};

const GREETER = path.join(bin('Greeter'), 'Greeter.exe');
const UPDATER = path.join(bin('Updater'), 'Updater.exe');
const MATHKIT = path.join(bin('MathKit'), 'MathKit.dll');
const SNPI = { elements: 'SNPI' };

// expect: C# çıktısında bulunmalı · gone: bulunmamalı · detect: tespit kimlikleri · verified: davranış doğrulaması
// (behaviour = giriş noktası/yoklama karşılaştırıldı) · fx: denenen yan etki API'leri · layers: yakalanan katman sayısı
const GREETER_TEXT = ['"Hello, "', '"Update channel: https://updates.example.com/greeter/stable"', '"Level three"', "'='"];
const FIXTURES = [
  { name: 'greeter-plain', file: 'Greeter.exe', make: () => readFileSync(GREETER), expect: GREETER_TEXT, detect: [], verified: true },
  { name: 'greeter-constants', file: 'Greeter.exe', make: () => confuse('greeter-constants', GREETER, [['constants', SNPI]]), expect: GREETER_TEXT, detect: ['confuserex', 'string-encryption'], gone: ['ConfusedBy'], verified: true },
  { name: 'greeter-ctrlflow', file: 'Greeter.exe', make: () => confuse('greeter-ctrlflow', GREETER, [['ctrl flow']]), expect: GREETER_TEXT, detect: ['confuserex', 'control-flow'], verified: true },
  { name: 'greeter-refproxy', file: 'Greeter.exe', make: () => confuse('greeter-refproxy', GREETER, [['ref proxy']]), expect: ['Console.WriteLine("Hello, "'], detect: ['confuserex', 'proxy-calls'], verified: true },
  { name: 'greeter-rename', file: 'Greeter.exe', make: () => confuse('greeter-rename', GREETER, [['rename']]), expect: ['"Level three"', 'Main('], detect: ['confuserex', 'renaming'], verified: true },
  { name: 'greeter-full', file: 'Greeter.exe', make: () => confuse('greeter-full', GREETER, [['constants', SNPI], ['ctrl flow'], ['ref proxy'], ['rename'], ['anti debug'], ['anti ildasm']]), expect: GREETER_TEXT, detect: ['confuserex', 'string-encryption', 'anti-debug'], verified: true },
  { name: 'updater-full', file: 'Updater.exe', make: () => confuse('updater-full', UPDATER, [['constants', SNPI], ['ctrl flow'], ['ref proxy'], ['rename'], ['anti debug'], ['resources']]), expect: ['"https://updates.peel-demo.invalid/channel/stable.txt"', '"Updater.motd.txt"', '"Checksum: "'], detect: ['confuserex', 'string-encryption', 'resource-encryption'], verified: true, layers: 1, fx: ['System.Net.WebClient::DownloadString', 'System.IO.File::Exists'] },
  { name: 'updater-antitamper', file: 'Updater.exe', make: () => confuse('updater-antitamper', UPDATER, [['anti tamper'], ['constants', SNPI]]), expect: [], detect: ['confuserex', 'anti-tamper'], verified: false },
  { name: 'mathkit-constants', file: 'MathKit.dll', make: () => confuse('mathkit-constants', MATHKIT, [['constants', SNPI], ['ref proxy']]), expect: ['"abcdefghijklmnopqrstuvwxyz"', '"negative input"', '"positive "'], detect: ['confuserex', 'string-encryption'], verified: true },
  { name: 'mathkit-obfuscar', file: 'MathKit.dll', make: () => obfuscate('mathkit-obfuscar', MATHKIT), expect: ['"abcdefghijklmnopqrstuvwxyz"', '"negative input"', '" ("'], detect: ['obfuscar'], verified: true },
  { name: 'markers-commercial', file: 'Markers.dll', make: () => readFileSync(path.join(bin('Markers'), 'Markers.dll')), expect: ['"Hello, "'], detect: ['smartassembly', 'babel'], protection: true, verified: true },
  { name: 'loader-layer', file: 'Loader.exe', make: () => readFileSync(path.join(bin('Loader'), 'Loader.exe')), expect: ['"Loader: unpacking stage 2"'], detect: ['reflective-load', 'layer-captured'], verified: true, layers: 1, fx: ['System.Reflection.Assembly::Load'] },
];

mkdirSync(fixtures, { recursive: true });
if (!only) for (const f of readdirSync(fixtures)) if (/\.(x|json)$/.test(f)) rmSync(path.join(fixtures, f));
for (const fx of FIXTURES) {
  if (only && !fx.name.includes(only)) continue;
  const bytes = fx.make();
  writeFileSync(path.join(fixtures, `${fx.name}.x`), encodeFixture(bytes));
  const { make, name, ...meta } = fx;
  writeFileSync(path.join(fixtures, `${fx.name}.json`), `${JSON.stringify({ gone: [], fx: [], layers: 0, protection: false, ...meta }, null, 2)}\n`);
  console.log(`${name.padEnd(22)} ${String(bytes.length).padStart(7)} bayt`);
}
rmSync(work, { recursive: true, force: true });
console.log(`fixture'lar yazıldı: test/fixtures (${FIXTURES.length})`);
