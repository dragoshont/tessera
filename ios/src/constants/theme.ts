import { useColorScheme } from 'react-native'

const light = {
  background: '#f7f7f5', surface: '#ffffff', elevated: '#eef0ec', text: '#1c211e', muted: '#657069',
  line: '#d9ddd8', accent: '#286958', accentSoft: '#dcece7', warning: '#9a5b14', danger: '#a43b3b', success: '#237055',
  accentForeground: '#ffffff', accentForegroundMuted: '#ffffffcc', dangerForeground: '#ffffff', markTile: '#ffffff', tab: '#f8f9f7', shadow: '#13231d',
}
const dark = {
  background: '#111512', surface: '#191f1b', elevated: '#242b26', text: '#eef2ef', muted: '#a1aca5',
  line: '#333c36', accent: '#70bca4', accentSoft: '#203b32', warning: '#e0a55c', danger: '#ef8888', success: '#70c5a8',
  accentForeground: '#0b2119', accentForegroundMuted: '#0b2119cc', dangerForeground: '#2b0d0d', markTile: '#ffffff', tab: '#151a17', shadow: '#000000',
}

export const Colors = { light, dark }
export const Space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 }
export const Radius = { sm: 6, md: 8 }
export function usePalette() { return useColorScheme() === 'dark' ? dark : light }
