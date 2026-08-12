const path = require('node:path')
const { execFileSync } = require('node:child_process')

module.exports = async function signApp(context) {
  const appBundle = path.join(
    context.appOutDir,
    `${context.packager.appInfo.productFilename}.app`,
  )
  execFileSync('/usr/bin/codesign', [
    '--force',
    '--deep',
    '--sign',
    '-',
    '--identifier',
    context.packager.appInfo.id,
    appBundle,
  ])
}