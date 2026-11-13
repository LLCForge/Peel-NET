#!/usr/bin/env node
// Peel-NET komut satırı. Kullanım: peel-net <dosya|dizin|-> [-o çıktı.cs] [-a temiz.exe] [-r json|text|rapor.json] ... (peel-net --help)
import { runCli, EXIT } from '../src/cli/run.js';
import { UsageError } from '../src/cli/args.js';

try {
  process.exitCode = await runCli(process.argv.slice(2));
} catch (e) {
  if (e instanceof UsageError) {
    console.error(e.message);
    process.exitCode = EXIT.USAGE;
  } else {
    console.error(`Hata: ${e.message}`);
    process.exitCode = EXIT.ERROR;
  }
}
