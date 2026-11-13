// Peel-NET kütüphane API'si. .NET motoru WebAssembly içinde çalışır: Node'da izin modeliyle yalıtılmış alt süreçte,
// tarayıcıda Web Worker'da.
//   import { peel, deobfuscate, analyze } from 'peel-net';
//   const r = await peel(readFileSync('app.exe'));   // r.code (C#), r.bytes (temizlenmiş derleme), r.report
import { deobfuscate as runPipeline } from './core/pipeline.js';
import { analyze as runAnalyze } from './core/analyze.js';
import { PASSES } from './core/registry.js';

let shared = null;

export async function createSandbox() {
  if (typeof process !== 'undefined' && process.versions?.node) {
    const path = './sandbox/node-host.js';
    const { createNodeSandbox } = await import(/* @vite-ignore */ path);
    return createNodeSandbox();
  }
  const path = './sandbox/web-host.js';
  const { createWebSandbox } = await import(/* @vite-ignore */ path);
  return createWebSandbox();
}

async function sandboxOf(option) {
  if (option && typeof option === 'object') return option;
  shared ??= createSandbox();
  return shared;
}

/** Paylaşılan sandbox'ı kapatır (Node'da alt süreç; açık kalırsa süreç çıkmaz) */
export async function close() {
  if (!shared) return;
  const sb = await shared;
  shared = null;
  sb.close?.();
}

const toBytes = (input) => {
  if (input instanceof Uint8Array) return input;
  if (input instanceof ArrayBuffer) return new Uint8Array(input);
  throw new TypeError('girdi Uint8Array ya da ArrayBuffer olmalı (.NET derlemesi baytları)');
};

/** Yalnızca çözme: { code (C#), bytes (temizlenmiş derleme), stats, events, verified, ... } */
export async function deobfuscate(input, { sandbox, ...options } = {}) {
  return runPipeline(toBytes(input), { ...options, sandbox: await sandboxOf(sandbox) });
}

/** Çözme + analiz raporu */
export async function peel(input, { sandbox, lang = 'tr', ...options } = {}) {
  const bytes = toBytes(input);
  const r = await runPipeline(bytes, { ...options, sandbox: await sandboxOf(sandbox) }).catch((e) => ({ error: e.message }));
  const report = r.info?.net ? runAnalyze({
    info: r.info, first: r.trace?.first, events: r.events, stats: r.stats, output: r.code, inBytes: bytes.length,
    outBytes: r.bytes?.length ?? bytes.length, verified: r.verified, layers: r.layers, layerInfo: r.layerInfo, lang,
  }) : null;
  return { ...r, report };
}

export const analyze = runAnalyze;
export const passes = () => PASSES.map(({ id, title }) => ({ id, title }));
