// .NET WebAssembly motorunu derler: dotnet publish (engine/) → public/engine/_framework.
// Gerekli: .NET SDK 10 (wasm-tools iş yükü gerekmez). Çıktı .gitignore'dadır; npm run build ve npm test önce bunu çalıştırır.
//   node scripts/build-engine.js          değişiklik yoksa atlar
//   node scripts/build-engine.js --force
import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, readdirSync, rmSync, statSync, writeFileSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
const engine = path.join(root, 'engine');
const out = path.join(root, 'public', 'engine');
const stamp = path.join(out, '.stamp');

const newest = (dir) => readdirSync(dir, { withFileTypes: true })
  .filter((e) => !['bin', 'obj'].includes(e.name))
  .reduce((t, e) => Math.max(t, e.isDirectory() ? newest(path.join(dir, e.name)) : statSync(path.join(dir, e.name)).mtimeMs), 0);

const force = process.argv.includes('--force');
const src = String(newest(engine));
if (!force && existsSync(stamp) && readFileSync(stamp, 'utf8') === src && existsSync(path.join(out, '_framework', 'dotnet.js'))) {
  console.log('motor güncel (public/engine)');
  process.exit(0);
}

const dotnet = spawnSync('dotnet', ['--version'], { encoding: 'utf8' });
if (dotnet.status !== 0) {
  console.error('.NET SDK bulunamadı. Motoru derlemek için .NET 10 SDK gerekir: https://dotnet.microsoft.com/download');
  process.exit(1);
}
console.log(`motor derleniyor (dotnet ${dotnet.stdout.trim()}) ...`);
const r = spawnSync('dotnet', ['publish', path.join(engine, 'PeelNet.Engine.csproj'), '-c', 'Release', '-nologo', '-v', 'q'], { stdio: 'inherit' });
if (r.status !== 0) process.exit(r.status ?? 1);
const fw = path.join(engine, 'bin', 'Release', 'net10.0', 'publish', 'wwwroot', '_framework');
rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
// sıkıştırılmış kopyalar (.br/.gz) sunucuya bırakılır; depoda yalnızca ham dosyalar
cpSync(fw, path.join(out, '_framework'), { recursive: true, filter: (f) => !/\.(br|gz)$/.test(f) });
writeFileSync(stamp, src);
console.log('motor hazır: public/engine/_framework');
