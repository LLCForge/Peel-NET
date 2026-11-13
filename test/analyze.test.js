// Rapor birimleri: IOC, tehdit kuralları, tespit çevirisi ve özet. Şüpheli dizgeler parçalı yazılır.
import assert from 'node:assert/strict';
import { iocs } from '../src/report/iocs.js';
import { threat } from '../src/report/threat.js';
import { detect } from '../src/report/detect.js';
import { analyze, literalsOf } from '../src/core/analyze.js';

// IOC: sürüm numarası IP sayılmaz, Microsoft/şema alanları zararsız ayrılır
const io = iocs(['Version=1.0.0.0', 'connect 203.0.113.7:4444', 'https://evil.' + 'example.invalid/a.bin', 'http://schemas.microsoft.com/x', 'mail bad@actor.invalid']);
assert.deepEqual(io.ips, ['203.0.113.7']);
assert.deepEqual(io.urls, ['https://evil.example.invalid/a.bin']);
assert.ok(io.benign.includes('schemas.microsoft.com'));
assert.deepEqual(io.emails, ['bad@actor.invalid']);

// tehdit: P/Invoke enjeksiyon üçlüsü kritik, tarayıcı verisi + DPAPI çalıcı
const inj = threat({ refs: [], pinvoke: ['kernel32.dll!' + 'Virtual' + 'AllocEx', 'kernel32.dll!' + 'Write' + 'ProcessMemory', 'kernel32.dll!' + 'Create' + 'RemoteThread'], strings: [], fx: [], detections: [] }, 'en');
assert.equal(inj.classes[0].id, 'injection');
assert.ok(inj.score >= 75);
const steal = threat({ refs: ['System.Security.Cryptography.ProtectedData::Unprotect'], pinvoke: [], strings: ['\Google\Chrome\User ' + 'Data\Default\Login ' + 'Data'], fx: [], detections: [] }, 'tr');
assert.equal(steal.classes[0].id, 'stealer');
assert.equal(threat({ refs: ['System.Console::WriteLine'], pinvoke: [], strings: ['hello'], fx: [], detections: [] }).level, 'none');
// yalnızca kaynak içeren katman (ConfuserEx resources) yükleyici sayılmaz
const resOnly = threat({ refs: [], pinvoke: [], strings: [], fx: [{ api: 'System.Reflection.Assembly::Load', cat: 'load', args: ['byte[100]'] }], detections: [], codeLayers: 0 });
assert.ok(!resOnly.classes.some((c) => c.id === 'reflective-loader'));

// tespit çevirisi
const d = detect([{ id: 'string-encryption', name: 'Dizge/sabit şifreleme', kind: 'technique', confidence: 0.85, decodable: true, evidence: ['5 çözücü, 40 çağrı yeri'] }], null, 'en');
assert.equal(d[0].name, 'String/constant encryption');
assert.equal(d[0].evidence[0], '5 decryptor(s), 40 call site(s)');

// C# dizge sabitleri
assert.deepEqual(literalsOf('Console.WriteLine("a\\"b"); var x = "https://x.invalid/";'), ['a"b', 'https://x.invalid/']);

// özet: TR ve EN
const info = { info: { name: 'A.exe', kind: 'exe', framework: '.NETFramework,Version=v4.8', types: 3, methods: 9 }, detections: [], strings: [], refs: [], pinvoke: [] };
const tr = analyze({ info, lang: 'tr', stats: { strings: 4 }, verified: true });
const en = analyze({ info, lang: 'en', stats: { strings: 4 }, verified: true });
assert.match(tr.summary, /4 dizge/);
assert.match(en.summary, /4 string\(s\)/);
assert.equal(en.threat.level, 'none');
console.log('analyze: tamam');
