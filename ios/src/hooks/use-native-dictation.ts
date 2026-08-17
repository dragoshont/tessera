import { useEffect, useRef, useState } from 'react'
import { AppState, Linking } from 'react-native'
import { ExpoSpeechRecognitionModule, useSpeechRecognitionEvent } from 'expo-speech-recognition'

import {
  classifyDictationError,
  decideDictationPermission,
  isDictationCapturing,
  mergeDictationDraft,
  nextDictationState,
  type DictationState,
} from '@/hooks/dictation-state'
import type { MicrophoneLease } from '@/hooks/microphone-lease'

export type NativeDictationState = DictationState

export function useNativeDictation({ draft, onDraftChange, microphoneLease, disabled = false }: {
  draft: string
  onDraftChange: (value: string) => void
  microphoneLease: MicrophoneLease
  disabled?: boolean
}) {
  const [state, setState] = useState<NativeDictationState>('IDLE')
  const baseDraft = useRef(draft)
  const active = useRef(false)
  const mounted = useRef(true)
  const disabledRef = useRef(disabled)
  const expectedAbort = useRef(false)
  const onDraftChangeRef = useRef(onDraftChange)
  onDraftChangeRef.current = onDraftChange
  disabledRef.current = disabled

  const abort = (nextState: NativeDictationState = 'INTERRUPTED') => {
    if (!active.current) return
    expectedAbort.current = true
    active.current = false
    ExpoSpeechRecognitionModule.abort()
    microphoneLease.release('dictation')
    setState(nextState)
  }

  const start = async () => {
    if (disabled || active.current || !microphoneLease.acquire('dictation')) return
    setState('REQUESTING_PERMISSION')
    try {
      let permission = await ExpoSpeechRecognitionModule.getPermissionsAsync()
      if (decideDictationPermission(permission) === 'REQUEST') {
        permission = await ExpoSpeechRecognitionModule.requestPermissionsAsync()
      }
      const decision = decideDictationPermission(permission, mounted.current, AppState.currentState === 'active', !disabledRef.current)
      if (decision !== 'START') {
        microphoneLease.release('dictation')
        if (mounted.current) setState(decision === 'REQUEST' ? 'PERMISSION_DENIED' : decision)
        return
      }

      baseDraft.current = draft
      expectedAbort.current = false
      active.current = true
      ExpoSpeechRecognitionModule.start({
        lang: Intl.DateTimeFormat().resolvedOptions().locale,
        interimResults: true,
        continuous: false,
        maxAlternatives: 1,
        addsPunctuation: true,
        iosTaskHint: 'dictation',
      })
    } catch {
      active.current = false
      microphoneLease.release('dictation')
      setState('UNAVAILABLE')
    }
  }

  const stop = () => {
    if (!active.current) return
    setState('PROCESSING')
    ExpoSpeechRecognitionModule.stop()
  }

  useSpeechRecognitionEvent('start', () => mounted.current && setState((current) => nextDictationState(current, 'START')))
  useSpeechRecognitionEvent('result', (event) => {
    if (!mounted.current) return
    const transcript = event.results[0]?.transcript ?? ''
    onDraftChangeRef.current(mergeDictationDraft(baseDraft.current, transcript))
    setState((current) => nextDictationState(current, event.isFinal ? 'FINAL_RESULT' : 'PARTIAL_RESULT'))
  })
  useSpeechRecognitionEvent('error', (event) => {
    if (!mounted.current) return
    active.current = false
    microphoneLease.release('dictation')
    if (expectedAbort.current && event.error === 'aborted') return
    setState(classifyDictationError(event.error))
  })
  useSpeechRecognitionEvent('end', () => {
    if (!mounted.current) return
    active.current = false
    microphoneLease.release('dictation')
    expectedAbort.current = false
    setState((current) => nextDictationState(current, 'END'))
  })

  useEffect(() => {
    if (disabled && active.current) abort()
  }, [disabled])

  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next) => {
      if (next !== 'active') abort()
    })
    return () => {
      mounted.current = false
      microphoneLease.release('dictation')
      subscription.remove()
      if (active.current) {
        expectedAbort.current = true
        active.current = false
        ExpoSpeechRecognitionModule.abort()
      }
    }
  }, [])

  return {
    state,
    active: isDictationCapturing(state),
    start,
    stop,
    abort,
    openSettings: () => Linking.openSettings(),
  }
}