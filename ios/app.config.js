const remoteOrigin = process.env.EXPO_PUBLIC_TESSERA_REMOTE_ORIGIN ?? 'https://tessera.example'
const localOrigin = process.env.EXPO_PUBLIC_TESSERA_LOCAL_ORIGIN ?? ''
const serverId = process.env.EXPO_PUBLIC_TESSERA_SERVER_ID ?? 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'

export default {
  expo: {
    name: 'Tessera',
    slug: 'tessera',
    version: '0.1.0',
    orientation: 'portrait',
    icon: './assets/images/icon.png',
    scheme: 'tessera',
    userInterfaceStyle: 'automatic',
    ios: {
      bundleIdentifier: 'io.tessera.mobile',
      supportsTablet: true,
      config: { usesNonExemptEncryption: false },
      infoPlist: {
        NSFaceIDUsageDescription: 'Use Face ID to unlock your Tessera session.',
        NSMicrophoneUsageDescription: 'Tessera uses the microphone only while you explicitly dictate a draft or run a realtime voice conversation.',
        NSSpeechRecognitionUsageDescription: 'Tessera uses Apple Speech Recognition only while you explicitly dictate text into a message draft.',
      },
    },
    android: {
      package: 'io.tessera.mobile',
      adaptiveIcon: {
        backgroundColor: '#111827',
        foregroundImage: './assets/images/android-icon-foreground.png',
      },
      predictiveBackGestureEnabled: true,
    },
    plugins: [
      'expo-router',
      'expo-font',
      ['expo-splash-screen', { backgroundColor: '#111827', image: './assets/images/splash-icon.png', imageWidth: 76 }],
      ['expo-secure-store', { faceIDPermission: 'Use Face ID to unlock your Tessera session.' }],
      ['expo-local-authentication', { faceIDPermission: 'Use Face ID to unlock Tessera.' }],
      ['expo-notifications', { enableBackgroundRemoteNotifications: false }],
      ['expo-speech-recognition', {
        microphonePermission: 'Tessera uses the microphone only while you explicitly dictate a draft or run a realtime voice conversation.',
        speechRecognitionPermission: 'Tessera uses Apple Speech Recognition only while you explicitly dictate text into a message draft.',
      }],
    ],
    experiments: { typedRoutes: true, reactCompiler: true },
    extra: { tessera: { serverId, remoteOrigin, localOrigin, clientVersion: '0.1.0' } },
  },
}