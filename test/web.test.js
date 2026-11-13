// Tarayıcı yolunun Node'daki taklidi: web-worker.js'teki gibi dotnet.js doğrudan içe aktarılır ve aynı ileti işleyicisi
// (engine.js) kullanılır. Örnek derleme (src/sample.js) çözülebilmeli, _headers kuralları ayrıştırılmalı ve worker
// CSP'si dış ağı kapatmalı.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import { createEngine, startRuntime } from '../src/sandbox/engine.js';
import { sandboxApi } from '../src/sandbox/api.js';
import { deobfuscate } from '../src/core/pipeline.js';
import { sampleBytes, SAMPLE_NAME } from '../src/sample.js';
import { parseHeaders } from '../vite.config.js';
import { engineDir } from '../src/sandbox/node-host.js';

const engine = createEngine(async () => startRuntime(await import(pathToFileURL(path.join(engineDir(), 'dotnet.js')).href)));
const sandbox = sandboxApi((msg) => engine.handle(msg));

const res = await deobfuscate(sampleBytes(), { sandbox, name: SAMPLE_NAME, timeline: false });
assert.equal(res.verified, true, 'örnek davranış doğrulanamadı');
assert.ok(res.code.includes('"https://updates.peel-demo.invalid/channel/stable.txt"'), 'örnek dizgeleri çözülmedi');
assert.ok(res.code.includes('Console.WriteLine("Config -> " + '), 'vekil çağrılar çözülmedi');
assert.ok(!res.events.some((e) => e.state === 'fail'), 'geri alınan geçiş var');

// barındırma başlıkları
const rules = parseHeaders(readFileSync(new URL('../public/_headers', import.meta.url), 'utf8'));
const worker = rules.find((r) => r.path === '/assets/web-worker-*')?.headers['Content-Security-Policy'] ?? '';
assert.match(worker, /default-src 'none'/);
assert.match(worker, /connect-src 'self'/);
assert.doesNotMatch(worker, /\s'unsafe-eval'|'unsafe-inline'/);
assert.ok(rules.find((r) => r.path === '/index.html')?.headers['Content-Security-Policy'].includes("frame-ancestors 'none'"));
console.log(`web: örnek ${res.code.split('\n').length} satır C#, ${rules.length} başlık kuralı`);
process.exit(0);
