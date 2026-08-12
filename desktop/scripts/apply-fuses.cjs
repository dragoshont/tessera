const path = require('node:path')
const { execFileSync } = require('node:child_process')
const {
  flipFuses,
  FuseVersion,
  FuseV1Options,
} = require('@electron/fuses')

module.exports = async function applyFuses(context) {
  const appBundle = path.join(
    context.appOutDir,
    `${context.packager.appInfo.productFilename}.app`,
  )
  const binary = path.join(
    appBundle,
    'Contents',
    'MacOS',
    context.packager.appInfo.productFilename,
  )
  await flipFuses(binary, {
    version: FuseVersion.V1,
    [FuseV1Options.RunAsNode]: false,
    [FuseV1Options.EnableCookieEncryption]: true,
    [FuseV1Options.EnableNodeOptionsEnvironmentVariable]: false,
    [FuseV1Options.EnableNodeCliInspectArguments]: false,
    [FuseV1Options.EnableEmbeddedAsarIntegrityValidation]: true,
    [FuseV1Options.OnlyLoadAppFromAsar]: true,
    [FuseV1Options.GrantFileProtocolExtraPrivileges]: false,
  })
  const plist = path.join(appBundle, 'Contents', 'Info.plist')
  execFileSync('/usr/libexec/PlistBuddy', [
    '-c',
    'Set :NSAppTransportSecurity:NSAllowsArbitraryLoads false',
    plist,
  ])
  execFileSync('/usr/libexec/PlistBuddy', [
    '-c',
    'Set :NSAppTransportSecurity:NSAllowsLocalNetworking false',
    plist,
  ])
  try {
    execFileSync('/usr/libexec/PlistBuddy', [
      '-c',
      'Delete :NSAppTransportSecurity:NSExceptionDomains',
      plist,
    ])
  } catch {
    // Electron packaging may eventually stop adding local development exceptions.
  }
}
