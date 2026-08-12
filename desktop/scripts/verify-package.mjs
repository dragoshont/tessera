import { access, readFile, readdir, stat } from 'node:fs/promises'
import path from 'node:path'

const release = path.resolve('release')
const app = path.join(release, 'mac-arm64', 'Tessera.app')
await access(app).catch(() => { throw new Error('Packaged Tessera.app is missing.') })
await access(path.join(app, 'Contents', 'MacOS', 'Tessera'))
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
    else if (info.size <= 8 * 1024 * 1024) {
      const text = await readFile(value).then((buffer) => buffer.toString('utf8')).catch(() => '')
      if (forbidden.some((pattern) => pattern.test(text))) throw new Error(`Possible secret in ${value}`)
    }
  }
}
await scan(app)
console.log(`PACKAGE_VERIFY: PASS (${app})`)
