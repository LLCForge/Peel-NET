// Bekçili sandbox'a istemci API'si (Node ve tarayıcı ortak). Baytlar Uint8Array'dir.
export const LOAD_DEADLINE_MS = 120_000;
export const RUN_TIMEOUT_MS = 20_000;
export const PASS_TIMEOUT_MS = 120_000;

export function sandboxApi(send) {
  let loaded = null;
  // çalışma zamanı yüklemesi uzun sürer: ilk istek ayrı (uzun) son tarihle
  const ready = async () => {
    const r = await send({ type: 'load', deadlineMs: LOAD_DEADLINE_MS });
    if (!r.ok) throw new Error(`.NET motoru yüklenemedi: ${r.error}`);
    loaded = r.version;
    return r;
  };
  const call = async (msg, deadlineMs) => {
    await ready();
    const r = await send({ ...msg, deadlineMs });
    return r;
  };
  return {
    version: () => loaded,
    ready,
    inspect: (bytes) => call({ type: 'inspect', bytes }, PASS_TIMEOUT_MS),
    /** giriş noktasını ve/veya yoklama yöntemlerini çalıştırır (davranış görüntüsü) */
    snapshot: (bytes, { main = true, probes = true, timeoutMs = RUN_TIMEOUT_MS } = {}) => call({ type: 'snapshot', bytes, main, probes }, timeoutMs),
    pass: (bytes, pass, options = {}) => call({ type: 'pass', bytes, pass, options }, PASS_TIMEOUT_MS),
    decompile: (bytes, mode = 'cs', name = 'module.dll') => call({ type: 'decompile', bytes, mode, name }, PASS_TIMEOUT_MS),
  };
}
