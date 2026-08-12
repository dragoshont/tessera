import { Tabs } from 'expo-router'
import { SymbolView, type SymbolViewProps } from 'expo-symbols'
import type { ColorValue } from 'react-native'

import { usePalette } from '@/constants/theme'

const icon = (name: SymbolViewProps['name']) => ({ color }: { color: ColorValue }) => <SymbolView name={name} size={22} tintColor={color} />

export default function TabLayout() {
  const palette = usePalette()
  return (
    <Tabs screenOptions={{
      tabBarActiveTintColor: palette.accent,
      tabBarInactiveTintColor: palette.muted,
      tabBarStyle: { backgroundColor: palette.tab, borderTopColor: palette.line },
      headerStyle: { backgroundColor: palette.background },
      headerShadowVisible: false,
      headerTintColor: palette.text,
    }}>
      <Tabs.Screen name="chat" options={{ title: 'Chat', tabBarIcon: icon('message.fill') }} />
      <Tabs.Screen name="jobs" options={{ title: 'Jobs', tabBarIcon: icon('calendar.badge.clock') }} />
      <Tabs.Screen name="accounts" options={{ title: 'Accounts', tabBarIcon: icon('person.2.fill') }} />
      <Tabs.Screen name="memory" options={{ title: 'Memory', tabBarIcon: icon('brain.head.profile') }} />
      <Tabs.Screen name="more" options={{ title: 'More', tabBarIcon: icon('ellipsis.circle.fill') }} />
    </Tabs>
  )
}