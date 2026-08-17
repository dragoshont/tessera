export type MicrophoneOwner = 'dictation' | 'realtime-voice'

export class MicrophoneLease {
  private owner: MicrophoneOwner | null = null

  acquire(owner: MicrophoneOwner): boolean {
    if (this.owner !== null) return false
    this.owner = owner
    return true
  }

  release(owner: MicrophoneOwner): void {
    if (this.owner === owner) this.owner = null
  }
}

export function closeMicrophoneCapture(lease: MicrophoneLease, owner: MicrophoneOwner, cleanup: () => void): void {
  try {
    cleanup()
  } finally {
    lease.release(owner)
  }
}