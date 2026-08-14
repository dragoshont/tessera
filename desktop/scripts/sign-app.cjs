const path = require('node:path')
const fs = require('node:fs')
const { execFileSync } = require('node:child_process')

function collectCode(root, output = []) {
  for (const entry of fs.readdirSync(root)) {
    const value = path.join(root, entry)
    const info = fs.lstatSync(value)
    if (info.isSymbolicLink()) continue
    if (info.isDirectory()) {
      collectCode(value, output)
      if (/\.(app|framework|xpc|appex)$/.test(entry)) output.push(value)
    } else if (info.isFile() && (info.mode & 0o111 || /\.(dylib|node)$/.test(entry))) {
      output.push(value)
    }
  }
  return output
}

function sign(value, identity, extra = []) {
  const signatureOptions = identity === '-'
    ? ['--timestamp=none']
    : ['--options', 'runtime', '--timestamp']
  execFileSync('/usr/bin/codesign', [
    '--force',
    '--sign',
    identity,
    ...signatureOptions,
    ...extra,
    value,
  ])
}

module.exports = async function signApp(context) {
  const appBundle = path.join(
    context.appOutDir,
    `${context.packager.appInfo.productFilename}.app`,
  )
  const loginItem = path.join(appBundle, 'Contents', 'Library', 'LoginItems', 'TesseraMacHost.app')
  const control = path.join(appBundle, 'Contents', 'Resources', 'TesseraHostControl')
  const entitlements = path.resolve(__dirname, '../../mac-host/dist/TesseraMacHost.entitlements')
  const identity = context.packager.platformSpecificBuildOptions.identity || '-'
  const nested = collectCode(path.join(appBundle, 'Contents'))
    .filter((value) => value !== loginItem && value !== control && !value.startsWith(`${loginItem}${path.sep}`))
    .sort((left, right) => right.split(path.sep).length - left.split(path.sep).length)
  for (const value of nested) sign(value, identity)
  sign(loginItem, identity, [
    '--identifier',
    'ro.hont.tessera.host',
    '--entitlements',
    entitlements,
  ])
  sign(control, identity, [
    '--identifier',
    'ro.hont.tessera.host.control',
    '--entitlements',
    entitlements,
  ])
  sign(appBundle, identity, [
    '--identifier',
    context.packager.appInfo.id,
  ])
  execFileSync('/usr/bin/codesign', ['--verify', '--deep', '--strict', appBundle])
}