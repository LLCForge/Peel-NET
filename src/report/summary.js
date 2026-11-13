// Raporun tek paragraflık özeti (tr | en); yalnızca bulunan bulgulara dayanır.

const LEVEL = {
  tr: { none: 'yok', low: 'düşük', medium: 'orta', high: 'yüksek', critical: 'kritik' },
  en: { none: 'none', low: 'low', medium: 'medium', high: 'high', critical: 'critical' },
};

const list = (xs, en) => (xs.length <= 1 ? xs.join('') : `${xs.slice(0, -1).join(', ')} ${en ? 'and' : 've'} ${xs.at(-1)}`);

export function summarize(r, lang = 'tr') {
  const en = lang === 'en';
  const parts = [];
  const pct = (c) => (en ? `${Math.round(c * 100)}%` : `%${Math.round(c * 100)}`);
  const name = (d) => `${d.name}${d.version ? ` ${d.version}` : ''} (${pct(d.confidence)})`;
  const of = (kind) => r.detection.filter((d) => d.kind === kind);
  const i = r.info;
  if (i) {
    parts.push(en
      ? `${i.name} is a .NET ${i.kind === 'dll' ? 'library' : 'program'} (${i.framework || i.runtime}) with ${i.types} types and ${i.methods} methods.`
      : `${i.name}, ${i.framework || i.runtime} hedefli bir .NET ${i.kind === 'dll' ? 'kütüphanesi' : 'programı'} (${i.types} tür, ${i.methods} yöntem).`);
  }
  if (r.protection) {
    const p = r.protection;
    parts.push(en
      ? `It is protected with ${p.product}; the protected part is intentionally not decoded (bypassing commercial protection is out of scope), only reported.`
      : `Dosya ${p.product} ile korunuyor; korunan bölüm bilinçli olarak çözülmez (ticari korumayı aşmak kapsam dışı), yalnızca raporlanır.`);
  }
  if (of('obfuscator').length) parts.push(`Obfuscator: ${list(of('obfuscator').map(name), en)}.`);
  if (of('packer').length) parts.push(`${en ? 'Packer' : 'Paketleyici'}: ${list(of('packer').map(name), en)}.`);
  if (of('technique').length) parts.push(en ? `Techniques: ${list(of('technique').map((d) => d.name), en)}.` : `Kullanılan teknikler: ${list(of('technique').map((d) => d.name), en)}.`);
  const m = r.metrics;
  const done = [
    [m.strings, 'dizge', 'string(s)'], [m.constants, 'sabit', 'constant(s)'], [m.flowMethods, 'düzleştirilmiş yöntem', 'flattened method(s)'],
    [m.proxyCalls, 'vekil çağrı', 'proxy call(s)'], [m.renamed, 'ad', 'name(s)'], [m.resources, 'kaynak', 'resource(s)'],
  ].filter(([n]) => n > 0).map(([n, tr, e]) => `${n} ${en ? e : tr}`);
  if (done.length) {
    parts.push(en
      ? `Recovered: ${list(done, en)}${m.verified ? '; every transform was checked in the sandbox against the original behaviour.' : '; behaviour could not be compared (structural checks only).'}`
      : `Geri kazanılan: ${list(done, en)}${m.verified ? '; her dönüşüm sandbox\'ta özgün davranışla karşılaştırıldı.' : '; davranış karşılaştırılamadı (yalnızca yapısal denetim).'}`);
  }
  if (r.layers?.length) {
    parts.push(en
      ? `${r.layers.length} in-memory assembly load(s) were captured in the sandbox (layers can be downloaded and analysed separately).`
      : `Sandbox'ta ${r.layers.length} bellekten derleme yükleme yakalandı (katmanlar ayrıca indirilip incelenebilir).`);
  }
  const th = r.threat;
  const lv = LEVEL[en ? 'en' : 'tr'][th.level];
  if (th.classes.length) {
    const cls = th.classes.slice(0, 5).map((c) => c.title);
    parts.push(en ? `Threat level ${lv} (${th.score}/100): ${list(cls, en)}.` : `Tehdit düzeyi ${lv} (${th.score}/100): ${list(cls, en)}.`);
  } else if (r.protection) {
    parts.push(en ? 'Because the code is protected, its behaviour could not be fully assessed.' : 'Kod korumalı olduğundan davranışı tam değerlendirilemedi.');
  } else {
    parts.push(en ? 'No dangerous capability was found (threat level none).' : 'Tehlikeli bir yetenek bulunmadı (tehdit düzeyi yok).');
  }
  const b = r.behaviour;
  const tried = b.network.length + b.commands.length + b.files.length + b.registry.length;
  if (tried) {
    parts.push(en
      ? `In the sandbox it attempted ${b.network.length} network, ${b.commands.length} process, ${b.files.length} file and ${b.registry.length} registry operation(s); none were performed.`
      : `Sandbox'ta ${b.network.length} ağ, ${b.commands.length} süreç, ${b.files.length} dosya ve ${b.registry.length} kayıt defteri işlemi denedi; hiçbiri gerçekleştirilmedi.`);
  }
  const io = r.iocs;
  const counts = [
    [io.urls, 'URL', 'URL(s)'], [io.domains, 'alan adı', 'domain(s)'], [io.ips, 'IP', 'IP(s)'], [io.emails, 'e-posta', 'e-mail(s)'],
    [io.discordWebhooks, 'Discord webhook', 'Discord webhook(s)'], [io.telegram, 'Telegram', 'Telegram'],
  ].filter(([xs]) => xs?.length).map(([xs, tr, e]) => `${xs.length} ${en ? e : tr}`);
  if (counts.length) parts.push(en ? `IOCs: ${list(counts, en)}.` : `IOC: ${list(counts, en)}.`);
  return parts.join(' ');
}
