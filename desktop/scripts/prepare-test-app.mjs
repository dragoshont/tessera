import { rm } from 'node:fs/promises'
import { execFileSync } from 'node:child_process'
import path from 'node:path'
import {
  flipFuses,
  FuseVersion,
  FuseV1Options,
} from '@electron/fuses'

const source = path.resolve('release/mac-arm64/Tessera.app')
const target = path.resolve('test-artifacts/Tessera.app')
await rm(path.dirname(target), { recursive: true, force: true })
execFileSync('/usr/bin/ditto', [source, target])
const binary = path.join(target, 'Contents', 'MacOS', 'Tessera')
await flipFuses(binary, {
  version: FuseVersion.V1,
  [FuseV1Options.EnableNodeCliInspectArguments]: true,
})
execFileSync('/usr/bin/codesign', [
  '--force',
  '--deep',
  '--sign',
  '-',
  '--identifier',
  'ro.hont.tessera.desktop.test',
  target,
])
console.log(`TEST_APP_READY: ${target}`)