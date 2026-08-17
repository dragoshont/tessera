import { useEffect } from 'react'
import { DarkTheme, DefaultTheme, Stack, ThemeProvider, router } from 'expo-router'
import * as Notifications from 'expo-notifications'
import { useColorScheme } from 'react-native'
import { isAllowedAppPath } from '@tessera/client'

import { AuthGate } from '@/components/auth-gate'
import { SessionProvider } from '@/providers/session'

Notifications.setNotificationHandler({
  handleNotification: async () => ({ shouldShowBanner: true, shouldShowList: true, shouldPlaySound: false, shouldSetBadge: true }),
})

function Navigation() {
  const scheme = useColorScheme()
  useEffect(() => {
    const open = (response: Notifications.NotificationResponse | null) => {
      const url = response?.notification.request.content.data?.url
      if (isAllowedAppPath(url)) router.push(url as never)
    }
    void Notifications.getLastNotificationResponseAsync().then(open)
    const subscription = Notifications.addNotificationResponseReceivedListener(open)
    return () => subscription.remove()
  }, [])
  return (
    <ThemeProvider value={scheme === 'dark' ? DarkTheme : DefaultTheme}>
      <AuthGate>
        <Stack screenOptions={{ headerBackTitle: 'Back' }}>
          <Stack.Screen name="index" options={{ headerShown: false }} />
          <Stack.Screen name="(tabs)" options={{ headerShown: false }} />
          <Stack.Screen name="action/[id]" options={{ title: 'Review action' }} />
          <Stack.Screen name="job-run/[id]" options={{ title: 'Development run' }} />
          <Stack.Screen name="remote" options={{ title: 'Remote Hosts' }} />
          <Stack.Screen name="remote-host/[id]" options={{ title: 'Remote Host' }} />
          <Stack.Screen name="plugins" options={{ title: 'Plugins' }} />
          <Stack.Screen name="activity" options={{ title: 'Activity' }} />
          <Stack.Screen name="settings" options={{ title: 'Settings' }} />
        </Stack>
      </AuthGate>
    </ThemeProvider>
  )
}

export default function RootLayout() {
  return <SessionProvider><Navigation /></SessionProvider>
}
