// Analiz raporu. Arayüz, CLI ve kütüphane bu biçimi kullanır; alanlar eklenebilir, var olanlar değişmez.
//
// Report = {
//   info:       { name, assembly, kind: 'exe'|'winexe'|'dll'|'netmodule', framework, runtime, machine, ilOnly, types, methods }
//   detection:  [{ id, name, kind: 'obfuscator'|'protector'|'packer'|'technique', confidence (0-1), version?, decodable, evidence: string[] }]
//   protection: null | { product, version, decodable: false, notes: string[] }   ticari koruma: yalnızca tanınır
//   threat:     { score (0-100), level: 'none'|'low'|'medium'|'high'|'critical', classes: [{ id, title, weight, evidence: string[] }] }
//   behaviour:  { network, commands, files, registry: [{ api, target }], loads: number, other: [{ api, args }] }
//               sandbox'ta denenen yan etkiler (hiçbiri gerçekten yapılmadı)
//   iocs:       { urls, domains, ips, emails, discordWebhooks, telegram, downloads, benign: string[] }
//   metrics:    { inBytes, outBytes, rounds, rollbacks, strings, constants, flowMethods, proxyCalls, renamed, resources, verified }
//   layers:     [{ index, size, pe, net, name?, types?, methods? }]   Assembly.Load(byte[]) ile yüklenmek istenen derlemeler
//   summary:    string
//   lang:       'tr'|'en'
// }
import { detect } from '../report/detect.js';
import { threat as assessThreat } from '../report/threat.js';
import { iocs as findIocs } from '../report/iocs.js';
import { summarize } from '../report/summary.js';

export function behaviourOf(fx = []) {
  const b = { network: [], commands: [], files: [], registry: [], loads: 0, other: [] };
  for (const f of fx) {
    const target = (f.args ?? []).filter((a) => !a.startsWith('<')).join(', ');
    switch (f.cat) {
      case 'network': b.network.push({ api: f.api, target }); break;
      case 'process': b.commands.push({ api: f.api, command: target }); break;
      case 'file': b.files.push({ api: f.api, path: target }); break;
      case 'registry': b.registry.push({ api: f.api, target }); break;
      case 'load': b.loads++; b.other.push({ api: f.api, args: f.args ?? [] }); break;
      default: b.other.push({ api: f.api, args: f.args ?? [] });
    }
  }
  return b;
}

/** C# çıktısındaki dizge sabitleri (çözülmüş dizgeler IOC ve tehdit taramasına katılır) */
export function literalsOf(code = '') {
  const out = [];
  for (const m of String(code).slice(0, 4_000_000).matchAll(/@?"((?:[^"\\\n]|\\.|"")*)"/g)) {
    out.push(m[1].replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\"/g, '"').replace(/\\\\/g, '\\'));
    if (out.length > 20000) break;
  }
  return out;
}

/**
 * @param {{ info: object, first?: object, events?: object[], stats?: object, output?: string, outBytes?: number, inBytes?: number,
 *           verified?: boolean, layers?: Uint8Array[], lang?: 'tr'|'en' }} o
 */
export function analyze({ info, first = null, events = [], stats = {}, output = '', inBytes = 0, outBytes = 0, verified = false, layers = [], layerInfo = [], lang = 'tr' }) {
  lang = lang === 'en' ? 'en' : 'tr';
  const fx = first?.fx ?? [];
  const detection = detect(info?.detections ?? [], first, lang);
  const prot = detection.filter((d) => d.kind === 'protector' && !d.decodable);
  const strings = [...new Set([...(info?.strings ?? []), ...literalsOf(output)])];
  const refs = (info?.refs ?? []).map(([k]) => k);
  const behaviour = behaviourOf(fx);
  const report = {
    info: info?.info ?? null,
    detection,
    protection: prot.length ? {
      product: prot.map((d) => d.name).join(', '),
      version: prot.find((d) => d.version)?.version ?? null,
      decodable: false,
      notes: prot.flatMap((d) => d.evidence).slice(0, 6),
    } : null,
    threat: assessThreat({ refs, pinvoke: info?.pinvoke ?? [], strings, fx, detections: detection, codeLayers: layerInfo.filter((l) => l.net && l.methods > 1).length }, lang),
    behaviour,
    iocs: findIocs([...strings, ...fx.flatMap((f) => f.args ?? [])], behaviour.network.map((n) => n.target)),
    metrics: {
      inBytes,
      outBytes,
      rounds: Math.max(0, ...events.map((e) => e.round ?? 0)),
      rollbacks: events.filter((e) => e.state === 'fail').length,
      strings: stats.strings ?? 0,
      constants: stats.constants ?? 0,
      flowMethods: stats.flattenedMethods ?? 0,
      proxyCalls: stats.proxyCalls ?? 0,
      renamed: ['types', 'methods', 'fields', 'properties', 'events', 'parameters', 'namespaces'].reduce((s, k) => s + (stats[k] ?? 0), 0),
      resources: stats.resources ?? 0,
      verified: Boolean(verified),
    },
    layers: layers.map((b, index) => ({ index, size: b.length, pe: b.length > 0x80 && b[0] === 0x4d && b[1] === 0x5a, ...(layerInfo[index] ?? {}) })),
    lang,
    summary: '',
  };
  report.summary = summarize(report, lang);
  return report;
}
