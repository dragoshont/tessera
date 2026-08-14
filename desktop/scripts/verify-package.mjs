import { createReadStream } from 'node:fs'
import { access, readFile, readdir, stat } from 'node:fs/promises'
import { execFileSync } from 'node:child_process'
import path from 'node:path'

const release = path.resolve('release')
const app = path.join(release, 'mac-arm64', 'Tessera.app')
await access(app).catch(() => { throw new Error('Packaged Tessera.app is missing.') })
await access(path.join(app, 'Contents', 'MacOS', 'Tessera'))
const loginItem = path.join(app, 'Contents', 'Library', 'LoginItems', 'TesseraMacHost.app')
const control = path.join(app, 'Contents', 'Resources', 'TesseraHostControl')
await access(path.join(loginItem, 'Contents', 'MacOS', 'TesseraMacHost'))
await access(control)
const helperInfo = await readFile(path.join(loginItem, 'Contents', 'Info.plist'), 'utf8')
if (!helperInfo.includes('ro.hont.tessera.host') || !helperInfo.includes('LSUIElement'))
  throw new Error('Nested Mac Host metadata is invalid.')
const forbidden = [
  /ghp_[A-Za-z0-9]{20,}/,
  /github_pat_[A-Za-z0-9_]{20,}/,
  /-----BEGIN [A-Z ]*PRIVATE KEY/,
  /RefreshTokenSSO/i,
  /RM_SUBSCRIPTION_KEY\s*[:=]\s*[A-Za-z0-9]{16,}/,
  /CLIENT_SECRET\s*[:=]\s*[^<\s][^\s]{10,}/,
]

async function scan(directory) {
  for (const entry of await readdir(directory)) {
    const value = path.join(directory, entry)
    const info = await stat(value)
    if (info.isDirectory()) await scan(value)
    else {
      let overlap = ''
      for await (const chunk of createReadStream(value, { highWaterMark: 64 * 1024 })) {
        const text = overlap + chunk.toString('latin1')
        if (forbidden.some((pattern) => pattern.test(text))) throw new Error(`Possible secret in ${value}`)
        overlap = text.slice(-512)
      }
    }
  }
}
await scan(app)
if ((await stat(control)).mode & 0o111 ? false : true) throw new Error('Mac Host control is not executable.')
execFileSync('/usr/bin/codesign', ['--verify', '--deep', '--strict', app], { stdio: 'inherit' })
function signedEntitlements(bundle) {
  return execFileSync('/usr/bin/codesign', ['-d', '--entitlements', ':-', bundle], {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'ignore'],
  })
}
function keychainGroups(plist) {
  const section = plist.match(/<key>keychain-access-groups<\/key>\s*<array>([\s\S]*?)<\/array>/)?.[1] ?? ''
  return [...section.matchAll(/<string>([^<]+)<\/string>/g)].map((match) => match[1]).sort()
}
const expectedEntitlements = await readFile(path.resolve('../mac-host/dist/TesseraMacHost.entitlements'), 'utf8')
const expectedGroups = keychainGroups(expectedEntitlements)
const helperGroups = keychainGroups(signedEntitlements(loginItem))
const controlGroups = keychainGroups(signedEntitlements(control))
const outerGroups = keychainGroups(signedEntitlements(app))
if (JSON.stringify(helperGroups) !== JSON.stringify(expectedGroups) || JSON.stringify(controlGroups) !== JSON.stringify(expectedGroups))
  throw new Error('Native Host signed Keychain groups do not match generated entitlements.')
if (outerGroups.length !== 0)
  throw new Error('Electron must not receive the Mac Host Keychain access group.')
console.log(`PACKAGE_VERIFY: PASS (${app})`)
