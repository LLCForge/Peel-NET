// Rapor modüllerinin ortak yardımcıları. Tarayıcıda da çalışır (Node'a özgü API yok).

export const t = (tr, en = tr) => ({ tr, en });
export const pick = (o, lang) => (lang === 'en' ? o.en : o.tr);

/** Antivirüs imzalarına takılmasın diye şüpheli dizgeler kaynakta parçalı yazılır: rx(['Virtual', 'AllocEx']) */
export const rx = (parts, flags = '') => new RegExp(parts.join(''), flags);

/** Kanıt için tek satırlık, kısaltılmış kesit */
export function snippet(s, max = 120) {
  const one = String(s).replace(/\s+/g, ' ').trim();
  return one.length > max ? `${one.slice(0, max - 1)}…` : one;
}

export const LEVELS = ['none', 'low', 'medium', 'high', 'critical'];
export const levelOf = (score) => (score <= 0 ? 'none' : score < 25 ? 'low' : score < 50 ? 'medium' : score < 75 ? 'high' : 'critical');

const LEVEL = {
  tr: { none: 'yok', low: 'düşük', medium: 'orta', high: 'yüksek', critical: 'kritik' },
  en: { none: 'none', low: 'low', medium: 'medium', high: 'high', critical: 'critical' },
};

/** Düz metin rapor (CLI --report text) */
export function reportText(r, lang = r?.lang ?? 'tr') {
  const en = lang === 'en';
  const L = (tr, e) => (en ? e : tr);
  const pct = (c) => (en ? `${Math.round(c * 100)}%` : `%${Math.round(c * 100)}`);
  const lines = [];
  lines.push(`${L('Özet', 'Summary')}: ${r.summary}`);
  lines.push(`${L('Tehdit', 'Threat')}: ${LEVEL[en ? 'en' : 'tr'][r.threat.level]} (${r.threat.score}/100)`);
  for (const c of r.threat.classes) {
    lines.push(`  - ${c.title}`);
    for (const e of c.evidence) lines.push(`      ${e}`);
  }
  if (r.protection) {
    lines.push(`${L('Koruma', 'Protection')}: ${r.protection.product} (${L('çözülmez, yalnızca rapor', 'not decoded, report only')})`);
    for (const n of r.protection.notes) lines.push(`  - ${n}`);
  }
  if (r.detection.length) {
    lines.push(`${L('Tespitler', 'Detections')}:`);
    for (const d of r.detection) {
      lines.push(`  - ${d.name}${d.version ? ` ${d.version}` : ''} [${d.kind}] ${pct(d.confidence)}${d.decodable ? '' : L(' (yalnızca rapor)', ' (report only)')}`);
      for (const e of d.evidence) lines.push(`      ${e}`);
    }
  }
  const b = r.behaviour;
  const acts = [...b.network.map((n) => `${n.api}(${n.target})`), ...b.commands.map((c) => `${c.api}(${c.command})`),
    ...b.files.map((f) => `${f.api}(${f.path})`), ...b.registry.map((x) => `${x.api}(${x.target})`), ...b.other.map((o) => `${o.api}(${o.args.join(', ')})`)];
  if (acts.length) {
    lines.push(`${L('Sandbox davranışı (yapılmadı, yalnızca kaydedildi)', 'Sandbox behaviour (recorded, never performed)')}:`);
    for (const a of acts.slice(0, 30)) lines.push(`  - ${snippet(a, 160)}`);
  }
  if (r.layers?.length) {
    lines.push(`${L('Katmanlar', 'Layers')}:`);
    for (const l of r.layers) lines.push(`  - #${l.index} ${l.size} ${L('bayt', 'bytes')}${l.net ? ` ${l.name} (${l.methods} ${L('yöntem', 'methods')})` : ''}`);
  }
  const io = r.iocs;
  const keys = ['urls', 'domains', 'ips', 'emails', 'discordWebhooks', 'telegram', 'downloads', 'benign'];
  if (keys.some((k) => io[k]?.length)) {
    lines.push('IOC:');
    for (const k of keys) if (io[k]?.length) lines.push(`  ${k}: ${io[k].join(', ')}`);
  }
  const m = r.metrics;
  lines.push(`${L('Ölçüler', 'Metrics')}: ${m.inBytes} → ${m.outBytes} ${L('bayt', 'bytes')}, ${L('tur', 'rounds')} ${m.rounds}, ${L('geri alma', 'rollbacks')} ${m.rollbacks}, ${L('dizge', 'strings')} ${m.strings}, ${L('sabit', 'constants')} ${m.constants}, ${L('akış', 'flow')} ${m.flowMethods}, ${L('vekil', 'proxy')} ${m.proxyCalls}, ${L('ad', 'renamed')} ${m.renamed}, ${L('doğrulandı', 'verified')}: ${m.verified ? L('evet', 'yes') : L('hayır', 'no')}`);
  return `${lines.join('\n')}\n`;
}
