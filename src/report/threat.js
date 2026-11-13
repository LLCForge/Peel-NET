// Tehdit analizi (.NET). Kanıt kaynakları: başvurulan API'ler (MemberRef), P/Invoke bildirimleri, dizgeler (ldstr ve
// çözülmüş dizgeler) ve sandbox'ta denenen yan etkiler (fx). Kod nasıl gizlenmiş olursa olsun çalışma anında denenen
// çağrı en güçlü kanıttır. Birden çok işaret gerektiren sınıflar yalnızca hepsi (ya da eşik kadarı) bulunduğunda sayılır.
// Şüpheli dizgeler kaynakta parçalı yazılır (antivirüs imzalarına takılmasın).
import { levelOf, pick, rx, snippet, t } from './text.js';

export const CLASSES = {
  injection: t('Süreç enjeksiyonu / hollowing', 'Process injection / hollowing'),
  'reflective-loader': t('Bellekten derleme yükleme (yükleyici)', 'Reflective assembly loading (loader)'),
  dropper: t('İndirip çalıştırma (dropper)', 'Download & execute (dropper)'),
  downloader: t('İndirici', 'Downloader'),
  'process-exec': t('Süreç / komut çalıştırma', 'Process / command execution'),
  keylogger: t('Tuş kaydı (keylogger)', 'Keylogging'),
  'screen-capture': t('Ekran görüntüsü alma', 'Screen capture'),
  stealer: t('Tarayıcı / cüzdan / oturum verisi çalma', 'Browser / wallet / session data theft'),
  persistence: t('Kalıcılık (başlangıçta çalışma)', 'Persistence (autostart)'),
  exfiltration: t('Veri sızdırma (Discord/Telegram)', 'Data exfiltration (Discord/Telegram)'),
  'defender-tamper': t('Antivirüs ayarlarını değiştirme', 'Antivirus tampering'),
  ransomware: t('Fidye yazılımı davranışı (toplu şifreleme)', 'Ransomware behaviour (mass encryption)'),
  'anti-analysis': t('Analiz/sanal makine tespiti', 'Anti-analysis / VM detection'),
  'native-code': t('Yerel kod çağırma (P/Invoke)', 'Native code invocation (P/Invoke)'),
};

const has = (set, ...names) => names.filter((n) => set.has(n));
const any = (list, re) => list.filter((s) => re.test(s));

// P/Invoke: "dll!İşlev" listesinden yalnızca işlev adları
const INJECT = ['Virtual', 'AllocEx'].join('');
const RULES_PINVOKE = [
  ['injection', 80, [INJECT, ['Write', 'ProcessMemory'].join(''), ['Create', 'RemoteThread'].join(''), ['NtUnmap', 'ViewOfSection'].join(''), ['ZwUnmap', 'ViewOfSection'].join(''), ['SetThread', 'Context'].join(''), ['Queue', 'UserAPC'].join('')], 2],
  ['keylogger', 60, [['GetAsync', 'KeyState'].join(''), ['SetWindows', 'HookEx'].join(''), ['GetKeyboard', 'State'].join(''), ['GetForeground', 'Window'].join('')], 2],
  ['screen-capture', 30, ['BitBlt', ['GetDesktop', 'Window'].join('')], 2],
  ['anti-analysis', 20, [['IsDebugger', 'Present'].join(''), ['CheckRemote', 'DebuggerPresent'].join(''), ['NtQuery', 'InformationProcess'].join('')], 1],
];

const STEALER = rx(['Login ', 'Data|Local ', 'State|\\\\Cookies\\b|User ', 'Data\\\\|wallet\\.', 'dat|tdata\\b|Local ', 'Storage\\\\leveldb|Exodus\\\\|Electrum\\\\|\\\\Mozilla\\\\Firefox\\\\Profiles|key4\\.', 'db|logins\\.', 'json'], 'i');
const PERSIST = rx(['Software\\\\Microsoft\\\\Windows\\\\Current', 'Version\\\\Run|schtasks|\\\\Start Menu\\\\Programs\\\\Start', 'up|Winlogon\\\\(?:Shell|Userinit)'], 'i');
const DEFENDER = rx(['Set-Mp', 'Preference|Add-Mp', 'Preference|Disable', 'RealtimeMonitoring|Exclusion', 'Path|Windows Defender\\\\Exclusions'], 'i');
const VM = rx(['vmware|virtualbox|vbox', 'service|qemu|SbieDll\\.', 'dll|wine_get_unix_file_name|sandboxie|cuckoo'], 'i');
const HOOK = rx(['discord(?:app)?\\.com/api/(?:v\\d+/)?', 'webhooks/'], 'i');
const TG = rx(['api\\.telegram\\.org/', 'bot'], 'i');
const SHELL = rx(['\\b(?:cmd\\.exe|powershell(?:\\.exe)?|wscript|cscript|mshta|regsvr32|rundll32)\\b'], 'i');
const RANSOM = rx(['\\.(?:locked|encrypted|crypt)\\b|your files (?:have been|are) encrypted|bit', 'coin|ransom|decrypt(?:or|ion) key'], 'i');

/**
 * @param {{ refs: string[], pinvoke: string[], strings: string[], fx: { api: string, args: string[] }[], detections: object[] }} ev
 * @param {'tr'|'en'} lang
 */
export function threat(ev, lang = 'tr') {
  const refs = new Set(ev.refs);
  const fxApis = new Set(ev.fx.map((f) => f.api));
  const fns = new Set(ev.pinvoke.map((p) => p.split('!').pop()));
  const found = new Map();
  const add = (id, weight, evidence) => {
    if (!weight || !evidence.length) return;
    const c = found.get(id) ?? { id, title: pick(CLASSES[id], lang), weight: 0, evidence: [] };
    c.weight = Math.max(c.weight, weight);
    for (const e of evidence) if (!c.evidence.includes(e) && c.evidence.length < 6) c.evidence.push(snippet(e, 100));
    found.set(id, c);
  };
  const ran = (label) => (lang === 'en' ? `sandbox: ${label}` : `sandbox: ${label}`);

  for (const [id, w, list, min] of RULES_PINVOKE) {
    const hits = list.filter((f) => fns.has(f));
    if (hits.length >= min) add(id, w, hits.map((h) => `P/Invoke ${h}`));
  }

  const download = has(refs, 'System.Net.WebClient::DownloadData', 'System.Net.WebClient::DownloadString', 'System.Net.WebClient::DownloadFile',
    'System.Net.Http.HttpClient::GetByteArrayAsync', 'System.Net.Http.HttpClient::GetAsync', 'System.Net.Http.HttpClient::GetStringAsync', 'System.Net.WebRequest::Create');
  const fxNet = ev.fx.filter((f) => f.api.startsWith('System.Net'));
  const exec = has(refs, 'System.Diagnostics.Process::Start');
  const fxExec = ev.fx.filter((f) => f.api === 'System.Diagnostics.Process::Start');
  const load = has(refs, 'System.Reflection.Assembly::Load', 'System.AppDomain::Load');
  const invoke = has(refs, 'System.Reflection.MethodBase::Invoke', 'System.Reflection.Assembly::get_EntryPoint', 'System.Reflection.Assembly::CreateInstance');
  const fxLoad = ev.fx.filter((f) => f.api.endsWith('::Load') && f.args.some((a) => a.startsWith('byte[')));

  if (download.length) add('downloader', 20, download);
  if (fxNet.length) add('downloader', 25, fxNet.slice(0, 3).map((f) => ran(`${f.api}(${f.args.filter((a) => !a.startsWith('<')).join(', ')})`)));
  if ((download.length || fxNet.length) && (exec.length || fxExec.length) && (refs.has('System.Net.WebClient::DownloadFile') || refs.has('System.IO.File::WriteAllBytes')))
    add('dropper', 60, [...download, ...exec, 'File.WriteAllBytes/DownloadFile']);
  if (load.length && invoke.length) add('reflective-loader', 45, [...load, ...invoke]);
  // yalnızca kod içeren katman yükleyici kanıtıdır (ConfuserEx resources gibi kaynak kapları sayılmaz)
  if (fxLoad.length && (ev.codeLayers ?? 1) > 0) add('reflective-loader', 55, fxLoad.map((f) => ran(`${f.api}(${f.args.join(', ')})`)));
  const shell = any(ev.strings, SHELL);
  if (exec.length || fxExec.length) add('process-exec', shell.length ? 45 : 30, [...exec, ...fxExec.map((f) => ran(`Process.Start(${f.args.join(', ')})`)), ...shell.slice(0, 2)]);

  const steal = any(ev.strings, STEALER);
  const dpapi = has(refs, 'System.Security.Cryptography.ProtectedData::Unprotect');
  if (steal.length >= 2 || (steal.length && dpapi.length)) add('stealer', 70, [...dpapi, ...steal.slice(0, 4)]);
  const persist = any(ev.strings, PERSIST);
  if (persist.length && (has(refs, 'Microsoft.Win32.RegistryKey::SetValue', 'Microsoft.Win32.Registry::SetValue').length || exec.length || refs.has('System.IO.File::Copy')))
    add('persistence', 45, persist.slice(0, 3));
  else if (persist.length) add('persistence', 25, persist.slice(0, 3));
  const hooks = [...any(ev.strings, HOOK), ...any(ev.strings, TG)];
  if (hooks.length) add('exfiltration', 50, hooks.slice(0, 3));
  const defender = any(ev.strings, DEFENDER);
  if (defender.length) add('defender-tamper', 60, defender.slice(0, 3));
  const crypto = has(refs, 'System.Security.Cryptography.SymmetricAlgorithm::CreateEncryptor', 'System.Security.Cryptography.Aes::Create', 'System.Security.Cryptography.RijndaelManaged::.ctor');
  const walk = has(refs, 'System.IO.Directory::GetFiles', 'System.IO.Directory::EnumerateFiles', 'System.IO.DirectoryInfo::GetFiles');
  const ransomText = any(ev.strings, RANSOM);
  if (crypto.length && walk.length && (ransomText.length || refs.has('System.IO.File::Delete'))) add('ransomware', 70, [...crypto, ...walk, ...ransomText.slice(0, 2)]);
  const vm = any(ev.strings, VM);
  const anti = ev.detections.filter((d) => ['anti-debug', 'anti-tamper', 'anti-dump'].includes(d.id)).map((d) => d.name);
  if (vm.length || anti.length) add('anti-analysis', vm.length ? 30 : 15, [...anti, ...vm.slice(0, 3)]);
  if (ev.pinvoke.length && !found.has('injection')) add('native-code', 5, ev.pinvoke.slice(0, 5));

  const classes = [...found.values()].sort((a, b) => b.weight - a.weight);
  // en güçlü sınıf tam, diğerleri yarım ağırlıkla
  const score = Math.min(100, Math.round(classes.reduce((s, c, i) => s + (i === 0 ? c.weight : c.weight / 2), 0)));
  return { score, level: levelOf(score), classes };
}
