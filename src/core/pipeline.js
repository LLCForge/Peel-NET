// Çözme hattı: statik inceleme → davranış temeli (sandbox'ta iki kez: kararsız alanlar ayıklanır) → değişiklik durana
// kadar geçiş turları → C# çıktısı. Her geçişten sonra motor modülü yazıp yeniden okur (yapısal doğrulama); temel
// varsa yeni modül sandbox'ta çalıştırılır ve çıktısı, istisnası, denenen yan etkileri ve yoklama sonuçları temelle
// karşılaştırılır. Fark varsa geçiş geri alınır. Ticari koruma ya da şifreli gövdeler (anti-tamper) tespit edilirse
// dönüşüm yapılmaz: yalnızca rapor.
import { PASSES } from './registry.js';

const MAX_ROUNDS = 6;

// istisna karşılaştırması: çerçeve türleri adıyla, kullanıcı türleri (yeniden adlandırılabilir) yalnızca "kullanıcı" olarak
const excType = (e) => {
  if (!e) return '';
  const type = String(e).split(':')[0];
  return /^System\.|^Microsoft\./.test(type) ? type : 'user';
};

function snapshotOf(r, ignore = new Set()) {
  const fx = (r.fx ?? []).filter((f) => !ignore.has(f.api));
  return {
    ran: Boolean(r.ran),
    out: r.out ?? '',
    exception: excType(r.exception),
    init: excType(r.init),
    exited: Boolean(r.exited),
    fx: JSON.stringify(fx.map((f) => [f.api, f.args])),
    probeFx: JSON.stringify((r.probeFx ?? []).filter((f) => !ignore.has(f.api)).map((f) => [f.api, f.args])),
    layers: ignore.size ? 0 : r.layers ?? 0,
    probes: new Map((r.probes ?? []).map((p) => [p.token, JSON.stringify(p.results)])),
  };
}

/** iki temel çalıştırmada farklı çıkan alanlar (zaman, rastgelelik) karşılaştırmadan çıkarılır */
function unstableOf(a, b) {
  const u = new Set();
  for (const k of ['ran', 'out', 'exception', 'init', 'exited', 'fx', 'probeFx', 'layers']) if (a[k] !== b[k]) u.add(k);
  for (const [tok, v] of a.probes) if (b.probes.get(tok) !== v) u.add(`probe:${tok}`);
  return u;
}

function diff(base, next, unstable) {
  for (const k of ['ran', 'out', 'exception', 'init', 'exited', 'fx', 'probeFx', 'layers']) if (!unstable.has(k) && base[k] !== next[k]) return k;
  for (const [tok, v] of base.probes) {
    if (unstable.has(`probe:${tok}`)) continue;
    if (next.probes.get(tok) !== v) return `probe 0x${tok.toString(16)}`;
  }
  return null;
}

const BLOCKING = new Set(['anti-tamper', 'invalid-bodies', 'proxy-delegates-only']);

/**
 * @param {Uint8Array} input .NET derlemesi (exe/dll)
 * @param {{ sandbox: object, name?: string, only?: string[]|null, onLog?: Function, onPass?: Function, onSnapshot?: Function,
 *           signal?: AbortSignal|null, decompile?: boolean, timeline?: boolean }} o
 */
export async function deobfuscate(input, { sandbox, name = 'input.dll', only = null, onLog = () => {}, onPass = () => {}, onSnapshot = () => {}, signal = null, decompile = true, timeline = true } = {}) {
  if (!sandbox) throw new Error('sandbox gerekli');
  const t0 = Date.now();
  const stats = {};
  const events = [];
  const log = (m, level = 'info') => onLog(m, level);
  const checkAbort = () => { if (signal?.aborted) throw new Error('Durduruldu'); };
  const bump = (k, n) => { if (n) stats[k] = (stats[k] ?? 0) + n; };

  const info = await sandbox.inspect(input);
  if (!info.ok) return { error: info.error ?? 'inceleme başarısız', events, stats, info: null };
  if (!info.net) return { error: info.error ?? 'Bir .NET derlemesi değil', events, stats, info, notNet: true };
  checkAbort();
  log(`${info.info.name} · ${info.info.kind} · ${info.info.framework || info.info.runtime} · ${info.info.types} tür, ${info.info.methods} yöntem`, 'info');
  for (const d of info.detections) log(`tespit: ${d.name}${d.version ? ` ${d.version}` : ''} (${Math.round(d.confidence * 100)}%)${d.decodable ? '' : ' · yalnızca rapor'}`, d.decodable ? 'info' : 'warn');

  // davranış temeli: iki kez alınır, farklı çıkan alanlar (zamana/rastgeleliğe bağlı) karşılaştırılmaz
  let first = await sandbox.snapshot(input);
  checkAbort();
  let baseline = null;
  let unstable = new Set();
  if (first.ok) {
    const again = await sandbox.snapshot(input);
    baseline = snapshotOf(first);
    if (again.ok) unstable = unstableOf(baseline, snapshotOf(again));
    if (unstable.size) log(`kararsız alanlar karşılaştırılmayacak: ${[...unstable].join(', ')}`, 'warn');
    const what = first.ran ? (first.mainOk ? 'giriş noktası çalıştı' : `giriş noktası: ${first.exception}`) : 'giriş noktası yok';
    log(`davranış temeli: ${what}; ${first.probes?.length ?? 0} yoklama, ${first.fx?.length ?? 0} yan etki denemesi`, 'info');
    if (!first.ran && !(first.probes?.length)) {
      log('çalıştırılabilir bir şey bulunamadı: yalnızca yapısal doğrulama', 'warn');
      baseline = null;
    }
  } else {
    log(`sandbox'ta çalıştırılamadı (${first.error}); yalnızca yapısal doğrulama`, 'warn');
    first = { ok: false, error: first.error, fx: [], layers: 0 };
  }

  // yakalanan katmanlar (Assembly.Load(byte[])) ayrıca incelenir: kod içeriyor mu, yalnızca kaynak mı (ConfuserEx resources)
  const layerInfo = [];
  for (const b of first.layerBytes ?? []) {
    const li = await sandbox.inspect(b);
    layerInfo.push(li.ok && li.net ? { net: true, name: li.info.name, types: li.info.types, methods: li.info.methods, detections: li.detections.map((d) => d.id) } : { net: false });
  }
  if (layerInfo.length) log(`${layerInfo.length} katman yakalandı: ${layerInfo.map((l) => (l.net ? `${l.name} (${l.methods} yöntem)` : 'PE/.NET değil')).join(', ')}`, 'info');

  const blockers = info.detections.filter((d) => !d.decodable && (d.kind === 'protector' || BLOCKING.has(d.id)));
  let code = input;
  const ignored = new Set();
  const shortNames = info.detections.some((d) => d.id === 'obfuscar' || d.id === 'dotfuscator');
  if (blockers.length) {
    log(`dönüşüm yapılmadı: ${blockers.map((d) => d.name).join(', ')} çözülmez, yalnızca raporlanır`, 'warn');
  } else {
    const passes = PASSES.filter((p) => !only || only.includes(p.id));
    for (let round = 1; round <= MAX_ROUNDS; round++) {
      let changed = false;
      for (const p of passes) {
        checkAbort();
        const t = Date.now();
        onPass(p.id, 'run');
        const r = await sandbox.pass(code, p.id, { shortNames });
        checkAbort();
        for (const line of r.log ?? []) log(`${p.id}: ${line}`, 'info');
        if (!r.ok) {
          log(`${p.id}: ${r.error}, geri alındı`, 'warn');
          events.push({ id: p.id, round, state: 'fail', reason: r.error, ms: Date.now() - t });
          onPass(p.id, 'fail');
          continue;
        }
        if (!r.changed) {
          events.push({ id: p.id, round, state: 'skip', ms: Date.now() - t });
          onPass(p.id, 'skip');
          continue;
        }
        if (baseline) {
          const after = await sandbox.snapshot(r.bytes);
          checkAbort();
          // geçişin bilerek kaldırdığı gizleyici yan etkileri (ör. gizli kaynak derlemesinin yüklenmesi) iki taraftan da düşülür
          for (const api of r.ignoreFx ?? []) ignored.add(api);
          const ref = ignored.size ? snapshotOf(first, ignored) : baseline;
          const why = after.ok ? diff(ref, snapshotOf(after, ignored), unstable) : `çalıştırılamadı: ${after.error}`;
          if (why) {
            log(`${p.id}: davranış değişti (${why}), geri alındı`, 'warn');
            events.push({ id: p.id, round, state: 'fail', reason: why, ms: Date.now() - t });
            onPass(p.id, 'fail');
            continue;
          }
        }
        code = r.bytes;
        changed = true;
        for (const [k, v] of Object.entries(r.stats ?? {})) bump(k, v);
        events.push({ id: p.id, round, state: 'ok', stats: r.stats, ms: Date.now() - t });
        onPass(p.id, 'ok');
        if (timeline && decompile) {
          const d = await sandbox.decompile(code, 'cs', name);
          onSnapshot({ id: p.id, round, code: d.ok ? d.text : `// ${d.error}` });
        }
      }
      if (!changed) break;
    }
  }

  let text = '';
  let mode = 'cs';
  if (decompile) {
    let d = await sandbox.decompile(code, 'cs', name);
    if (!d.ok) {
      log(`C# çıktısı üretilemedi (${d.error}); IL listesi gösteriliyor`, 'warn');
      d = await sandbox.decompile(code, 'il', name);
      mode = 'il';
    }
    text = d.ok ? d.text : `// ${d.error}`;
  }
  const final = baseline && code !== input ? await sandbox.snapshot(code) : null;
  return {
    code: text,
    text,
    mode,
    bytes: code,
    changed: code !== input,
    stats,
    events,
    info,
    blockers,
    verified: Boolean(baseline),
    trace: { first, final },
    layers: first.layerBytes ?? [],
    layerInfo,
    ms: Date.now() - t0,
  };
}
