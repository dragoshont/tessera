export type NotificationPermissionState = {
  label: 'NOT_DETERMINED' | 'DENIED' | 'AUTHORIZED' | 'PROVISIONAL' | 'EPHEMERAL'
  usable: boolean
  canAskAgain: boolean
}

export function notificationPermissionState(permission: {
  granted: boolean
  canAskAgain: boolean
  ios?: { status: number }
}): NotificationPermissionState {
  const status = permission.ios?.status
  if (status === 4) return { label: 'EPHEMERAL', usable: true, canAskAgain: permission.canAskAgain }
  if (status === 3) return { label: 'PROVISIONAL', usable: true, canAskAgain: permission.canAskAgain }
  if (status === 2 || permission.granted) return { label: 'AUTHORIZED', usable: true, canAskAgain: permission.canAskAgain }
  if (status === 1) return { label: 'DENIED', usable: false, canAskAgain: permission.canAskAgain }
  return { label: 'NOT_DETERMINED', usable: false, canAskAgain: permission.canAskAgain }
}