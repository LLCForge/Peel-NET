// Motorun tespitlerini (engine/Inspector.cs, Türkçe) rapor diline çevirir ve davranıştan gelen tespitleri ekler.
import { pick, t } from './text.js';

const NAMES = {
  renaming: t('Ad gizleme', 'Identifier renaming'),
  'string-encryption': t('Dizge/sabit şifreleme', 'String/constant encryption'),
  'proxy-calls': t('Vekil çağrılar', 'Proxy calls'),
  'proxy-delegates': t('Temsilci tabanlı vekiller (strong)', 'Delegate-based proxies (strong)'),
  'control-flow': t('Akış düzleştirme', 'Control-flow flattening'),
  'anti-tamper': t('Anti-tamper (şifreli yöntem gövdeleri)', 'Anti-tamper (encrypted method bodies)'),
  'anti-dump': t('Anti-dump', 'Anti-dump'),
  'anti-debug': t('Anti-debug', 'Anti-debug'),
  'invalid-bodies': t('Ayrıştırılamayan yöntem gövdeleri', 'Unparsable method bodies'),
  'suppress-ildasm': t('SuppressIldasm', 'SuppressIldasm'),
  'resource-encryption': t('Şifreli kaynaklar', 'Encrypted resources'),
  'embedded-assemblies': t('Gömülü PE/derlemeler', 'Embedded PE/assemblies'),
  'reflective-load': t('Bellekten derleme yükleme', 'In-memory assembly loading'),
  'layer-captured': t('Yakalanan katman (Assembly.Load)', 'Captured layer (Assembly.Load)'),
  nativeaot: t('NativeAOT (yerel derlenmiş .NET)', 'NativeAOT (natively compiled .NET)'),
  costura: t('Costura.Fody (gömülü bağımlılıklar)', 'Costura.Fody (embedded dependencies)'),
};

const EV = [
  [/^işaret: /, 'marker: '],
  [/^(\d+\/\d+) tür\/yöntem adı okunamaz$/, '$1 type/method names unreadable'],
  [/^(\d+) çözücü, (\d+) çağrı yeri$/, '$1 decryptor(s), $2 call site(s)'],
  [/^çözücü: /, 'decryptor: '],
  [/^(\d+) vekil yöntem$/, '$1 proxy method(s)'],
  [/^statik temsilci alanlarıyla dolaylı çağrılar \(çözülmez, raporlanır\)$/, 'indirect calls through static delegate fields (not decoded, reported)'],
  [/^(\d+) yöntemde durum değişkenli switch dağıtıcı$/, 'state-variable switch dispatcher in $1 method(s)'],
  [/^başlatıcı: /, 'initializer: '],
  [/^(\d+) gövde okunamadı \(şifreli ya da bozuk\)$/, '$1 bodies could not be read (encrypted or corrupt)'],
  [/^AppDomain çözümleme işleyicisi \+ /, 'AppDomain resolve handler + '],
  [/^gizli derlemeye bağlı kaynak: /, 'resource linked to hidden assembly: '],
  [/^kaynak: /, 'resource: '],
  [/^(\d+) ad '#=' önekli$/, "$1 names prefixed with '#='"],
  [/^filigran: /, 'watermark: '],
  [/^GUID'li /, 'GUID-named '],
  [/^dizge sınıfı: /, 'string class: '],
  [/^<Module> içinde (\d+) yöntem, genel çözücü\/koruma başlatıcısı$/, '<Module> has $1 methods, generic decryptor/guard initializer'],
  [/^PE bölümü: /, 'PE section: '],
  [/^sandbox: /, 'sandbox: '],
];

const nameEn = (name) => name.replace(/ \(çatal: ([^)]+)\)$/, ' (fork: $1)').replace(/ \(işaretsiz\)$/, ' (unmarked)');

/**
 * @param {object[]} detections motorun tespitleri
 * @param {object|null} first davranış temeli (fx, layers)
 */
export function detect(detections, first, lang = 'tr') {
  const list = detections.map((d) => ({
    id: d.id,
    name: NAMES[d.id] ? pick(NAMES[d.id], lang) : lang === 'en' ? nameEn(d.name) : d.name,
    kind: d.kind,
    confidence: d.confidence,
    ...(d.version ? { version: d.version } : {}),
    decodable: d.decodable,
    evidence: (d.evidence ?? []).map((e) => (lang === 'en' ? EV.reduce((s, [re, to]) => s.replace(re, to), e) : e)),
  }));
  const loads = (first?.fx ?? []).filter((f) => f.cat === 'load' && f.args.some((a) => a.startsWith('byte[')));
  if (loads.length) {
    list.push({
      id: 'layer-captured', name: pick(NAMES['layer-captured'], lang), kind: 'technique', confidence: 0.99, decodable: true,
      evidence: loads.slice(0, 3).map((f) => `sandbox: ${f.api}(${f.args.join(', ')})`),
    });
  }
  return list;
}
