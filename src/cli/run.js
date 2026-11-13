// peel-net komut satırı: tarayıcıdakiyle aynı .NET WebAssembly motoru, aynı sandbox (Node'da izin modeliyle yalıtılmış
// alt süreç).
import { readFile, writeFile, mkdir, readdir, stat } from 'node:fs/promises';
import { dirname, join, relative, resolve, basename, extname } from 'node:path';
import { peel, passes } from '../index.js';
import { createNodeSandbox } from '../sandbox/node-host.js';
import { reportText } from '../report/text.js';
import { parseArgs, UsageError } from './args.js';

export const EXIT = { OK: 0, ERROR: 1, USAGE: 2, THREAT: 4 };
export const MAX_INPUT = 64 * 1024 * 1024;
const LEVELS = ['none', 'low', 'medium', 'high', 'critical'];
const NET_FILE = /\.(?:exe|dll)$/i;

const SPEC = {
  out: { alias: 'o', type: 'string' },
  'out-dir': { alias: 'd', type: 'string' },
  assembly: { alias: 'a', type: 'string' },
  il: { type: 'bool' },
  report: { alias: 'r', type: 'string' },
  json: { type: 'bool' },
  layers: { type: 'string' },
  lang: { type: 'string' },
  only: { type: 'list' },
  'fail-on': { type: 'string' },
  quiet: { alias: 'q', type: 'bool' },
  help: { alias: 'h', type: 'bool' },
};

const HELP = {
  tr: `peel-net <dosya|dizin|-> [seçenekler]

.NET derlemelerini (exe/dll) WebAssembly içindeki .NET sandbox'ında çözer: dizge/sabit şifrelemesi, akış düzleştirme,
vekil çağrılar, şifreli kaynaklar, anti-debug ve gizlenmiş adlar. Çıktı okunur C# (ILSpy motoru) ve temizlenmiş derlemedir.
Ağ, süreç, dosya ve kayıt defteri çağrıları gerçekleştirilmez, yalnızca kaydedilir.

  -o, --out <dosya>        C# çıktısını dosyaya yaz (tek girdi; yoksa stdout)
  -d, --out-dir <dizin>    C# çıktılarını dizine yaz (dizin girdisinde yapı korunur)
  -a, --assembly <dosya>   temizlenmiş derlemeyi (exe/dll) dosyaya yaz (tek girdi)
      --il                 C# yerine IL listesi yaz
  -r, --report <hedef>     rapor: json | text (stdout'a, kod yazılmaz) ya da dosya yolu (.txt ise metin, değilse JSON;
                           dizin girdisinde JSON dizi)
      --json               --report json ile aynı
      --layers <dizin>     sandbox'ta yakalanan katmanları (Assembly.Load baytları) dizine yaz
      --only <geçiş,...>   yalnızca bu geçişler (${passes().map((p) => p.id).join(', ')})
      --lang tr|en         rapor dili (varsayılan tr)
      --fail-on <düzey>    tehdit düzeyi bu eşiğe ulaşırsa çıkış kodu 4: low | medium | high | critical
  -q, --quiet              ilerleme ve özet yazma
  -h, --help               bu yardım

Çıkış kodları: 0 tamam, 1 hata, 2 kullanım hatası, 4 --fail-on eşiğine ulaşıldı (hatadan önceliklidir).
Ticari korumalar (.NET Reactor, SmartAssembly, Babel VM, Agile.NET, ArmDot, VMProtect, Themida/WinLicense, KoiVM, DNGuard)
ve ConfuserEx anti-tamper tanınır ama bilinçli olarak çözülmez; yalnızca raporlanır.`,
  en: `peel-net <file|dir|-> [options]

Deobfuscates .NET assemblies (exe/dll) inside a .NET sandbox running in WebAssembly: string/constant encryption, control-flow
flattening, proxy calls, encrypted resources, anti-debug and obfuscated names. Output is readable C# (ILSpy engine) plus the
cleaned assembly. Network, process, file and registry calls are recorded, never performed.

  -o, --out <file>         write the C# output to a file (single input; otherwise stdout)
  -d, --out-dir <dir>      write C# outputs to a directory (directory input keeps its structure)
  -a, --assembly <file>    write the cleaned assembly (exe/dll) to a file (single input)
      --il                 write an IL listing instead of C#
  -r, --report <target>    report: json | text (to stdout, no code) or a file path (.txt gives text, anything else JSON;
                           a JSON array for directory input)
      --json               same as --report json
      --layers <dir>       write layers captured in the sandbox (Assembly.Load bytes) to a directory
      --only <pass,...>    run only these passes (${passes().map((p) => p.id).join(', ')})
      --lang tr|en         report language (default tr)
      --fail-on <level>    exit with code 4 when the threat level reaches: low | medium | high | critical
  -q, --quiet              no progress or summary output
  -h, --help               this help

Exit codes: 0 ok, 1 error, 2 usage error, 4 --fail-on threshold reached (takes precedence over errors).
Commercial protections (.NET Reactor, SmartAssembly, Babel VM, Agile.NET, ArmDot, VMProtect, Themida/WinLicense, KoiVM, DNGuard)
and ConfuserEx anti-tamper are identified but intentionally not decoded; they are reported only.`,
};

async function readBytes(path) {
  if (path === '-') {
    const chunks = [];
    let n = 0;
    for await (const c of process.stdin) {
      n += c.length;
      if (n > MAX_INPUT) throw new Error('Girdi çok büyük');
      chunks.push(c);
    }
    return new Uint8Array(Buffer.concat(chunks));
  }
  const s = await stat(path);
  if (!s.isFile()) throw new Error(`Dosya değil: ${path}`);
  if (s.size > MAX_INPUT) throw new Error(`Dosya çok büyük: ${path}`);
  return new Uint8Array(await readFile(path));
}

/** Dizindeki .exe/.dll dosyaları (özyinelemeli, sembolik bağlar izlenmez), sıralı */
async function listAssemblies(root) {
  const out = [];
  const walkDir = async (dir) => {
    const entries = await readdir(dir, { withFileTypes: true });
    for (const e of entries.sort((a, b) => a.name.localeCompare(b.name))) {
      const p = join(dir, e.name);
      if (e.isDirectory()) await walkDir(p);
      else if (e.isFile() && NET_FILE.test(e.name)) out.push(p);
    }
  };
  await walkDir(root);
  return out;
}

async function writeOut(path, data) {
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, data);
}

const levelOf = (entry) => entry.report?.threat?.level ?? 'none';
const textOf = (entries, isDir, lang) => entries.map((e) => `${isDir ? `== ${e.file} ==\n` : ''}${e.error ? `${lang === 'en' ? 'Error' : 'Hata'}: ${e.error}\n` : ''}${e.report ? reportText(e.report, lang) : ''}`).join('\n');

export async function runCli(argv) {
  const { opts, positional } = parseArgs(argv, SPEC);
  const lang = opts.lang === undefined ? 'tr' : opts.lang;
  if (lang !== 'tr' && lang !== 'en') throw new UsageError(`--lang tr ya da en olmalı: ${lang}`);
  if (opts.help) { console.log(HELP[lang]); return EXIT.OK; }
  if (positional.length !== 1) throw new UsageError(`${HELP[lang]}\n\nTek bir girdi gerekli (dosya, dizin ya da -).`);
  if (opts['fail-on'] !== undefined && !LEVELS.slice(1).includes(opts['fail-on'])) throw new UsageError('--fail-on şunlardan biri olmalı: low, medium, high, critical');
  const ids = passes().map((p) => p.id);
  const only = opts.only.flatMap((x) => x.split(',')).map((x) => x.trim()).filter(Boolean);
  const unknown = only.filter((x) => !ids.includes(x));
  if (unknown.length) throw new UsageError(`Bilinmeyen geçiş: ${unknown.join(', ')} (geçerli: ${ids.join(', ')})`);
  // -r json|text rapor biçimini stdout'a, başka değer dosya yolunu seçer
  const stdoutReport = opts.json ? 'json' : ['json', 'text'].includes(opts.report) ? opts.report : null;
  const reportFile = opts.report !== undefined && !['json', 'text'].includes(opts.report) ? opts.report : null;

  const target = positional[0];
  const isDir = target !== '-' && (await stat(target).catch(() => null))?.isDirectory();
  if (isDir && (opts.out || opts.assembly)) throw new UsageError('Dizin girdisinde -o/-a yerine -d kullanın');
  const log = (m) => { if (!opts.quiet) process.stderr.write(`${m}\n`); };
  const ext = opts.il ? '.il' : '.cs';

  const sandbox = createNodeSandbox();
  const entries = [];
  try {
    const files = isDir ? await listAssemblies(target) : [target];
    if (isDir && !files.length) log(lang === 'en' ? 'No .exe/.dll files found.' : '.exe/.dll dosyası bulunamadı.');
    for (const file of files) {
      const name = file === '-' ? '-' : isDir ? relative(target, file).split('\\').join('/') : file;
      const entry = { file: name, ok: false };
      entries.push(entry);
      let bytes;
      try {
        bytes = await readBytes(file);
      } catch (e) {
        entry.error = e.message;
        log(`${name}: ${e.message}`);
        continue;
      }
      const r = await peel(bytes, { sandbox, lang, name: file === '-' ? 'stdin.dll' : basename(file), timeline: false, ...(only.length ? { only } : {}) });
      let code = r.code ?? '';
      if (opts.il && r.bytes) {
        const d = await sandbox.decompile(r.bytes, 'il', basename(file));
        code = d.ok ? d.text : `// ${d.error}`;
      }
      Object.assign(entry, { ok: !r.error, error: r.error, verified: Boolean(r.verified), changed: Boolean(r.changed), ms: r.ms ?? 0, report: r.report });
      if (!r.error) {
        if (opts['out-dir']) {
          const rel = isDir ? relative(target, file) : file === '-' ? 'stdin.dll' : basename(file);
          const dest = resolve(opts['out-dir'], rel.slice(0, rel.length - extname(rel).length) + ext);
          if (relative(resolve(opts['out-dir']), dest).startsWith('..')) throw new Error(`Çıktı dizini dışına yazma: ${rel}`);
          await writeOut(dest, code);
        }
        if (opts.out) await writeOut(opts.out, code);
        else if (!isDir && !opts['out-dir'] && !stdoutReport) process.stdout.write(code);
        if (opts.assembly && r.bytes) await writeOut(opts.assembly, r.bytes);
        if (opts.layers && r.layers?.length) {
          const base = isDir ? relative(target, file).replace(/[\\/]/g, '_') : basename(file);
          for (const [i, b] of r.layers.entries()) await writeOut(join(opts.layers, `${base}.layer${i}.bin`), b);
        }
      }
      if (r.report) {
        const th = r.report.threat;
        log(`${isDir ? `${name}: ` : ''}[${th.level} ${th.score}/100]${r.error ? ` ${r.error};` : ''} ${r.report.summary}`);
      } else log(`${name}: ${r.error}`);
    }
  } finally {
    sandbox.close();
  }

  const payload = isDir ? entries : entries[0];
  const json = `${JSON.stringify(payload, null, 2)}\n`;
  if (reportFile) await writeOut(reportFile, /\.txt$/i.test(reportFile) ? textOf(entries, isDir, lang) : json);
  if (stdoutReport === 'json') process.stdout.write(json);
  else if (stdoutReport === 'text') process.stdout.write(textOf(entries, isDir, lang));

  const failOn = opts['fail-on'];
  if (failOn && entries.some((e) => LEVELS.indexOf(levelOf(e)) >= LEVELS.indexOf(failOn))) return EXIT.THREAT;
  return entries.some((e) => !e.ok) ? EXIT.ERROR : EXIT.OK;
}
