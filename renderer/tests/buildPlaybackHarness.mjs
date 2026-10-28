import { build } from 'vite'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

await build({
  configFile: false, logLevel: 'error',
  build: {
    lib: {
      entry: fileURLToPath(new URL('../src/playback/mediaElementController.ts', import.meta.url)),
      name: 'PlaybackHarness', formats: ['iife'], fileName: () => 'controller-harness.js',
    },
    outDir: join(tmpdir(), 'MusicPlayerStreamChecks', 'harness'), emptyOutDir: false,
  },
})
