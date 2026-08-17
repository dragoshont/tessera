import type { PropsWithChildren, ReactNode } from 'react'
import { ActivityIndicator, Pressable, StyleSheet, Text, View, type PressableProps, type ViewStyle } from 'react-native'
import { SymbolView, type SymbolViewProps } from 'expo-symbols'

import { Radius, Space, usePalette } from '@/constants/theme'
import { statusTone } from '@/components/status-tone'

export function Mark({ size = 36 }: { size?: number }) {
  const palette = usePalette()
  const inset = size * 0.19
  const gap = size * 0.1
  const tile = (size - inset * 2 - gap) / 2
  return (
    <View style={[styles.mark, { width: size, height: size, borderRadius: Math.max(7, size * 0.22), backgroundColor: palette.accent }]} accessibilityLabel="Tessera">
      {[0, 1, 2, 3].map((index) => <View key={index} style={[styles.tile, {
        width: tile,
        height: tile,
        left: inset + (index % 2) * (tile + gap),
        top: inset + Math.floor(index / 2) * (tile + gap),
        opacity: index === 1 || index === 2 ? 0.58 : 1,
        backgroundColor: palette.markTile,
      }]} />)}
    </View>
  )
}

export function Icon({ name, color, size = 20 }: { name: SymbolViewProps['name']; color?: string; size?: number }) {
  const palette = usePalette()
  return <SymbolView name={name} size={size} tintColor={color ?? palette.text} />
}

export function Card({ children, style }: PropsWithChildren<{ style?: ViewStyle }>) {
  const palette = usePalette()
  return <View style={[styles.card, { backgroundColor: palette.surface, borderColor: palette.line }, style]}>{children}</View>
}

export function SectionTitle({ children, detail }: PropsWithChildren<{ detail?: string }>) {
  const palette = usePalette()
  return <View style={styles.sectionTitle}><Text accessibilityRole="header" maxFontSizeMultiplier={2} style={[styles.sectionText, { color: palette.text }]}>{children}</Text>{detail ? <Text maxFontSizeMultiplier={2} style={[styles.detail, { color: palette.muted }]}>{detail}</Text> : null}</View>
}

type ButtonProps = PressableProps & { label: string; icon?: SymbolViewProps['name']; tone?: 'primary' | 'secondary' | 'danger'; busy?: boolean }
export function Button({ label, icon, tone = 'secondary', busy, disabled, style, ...props }: ButtonProps) {
  const palette = usePalette()
  const backgroundColor = tone === 'primary' ? palette.accent : tone === 'danger' ? palette.danger : palette.elevated
  const color = tone === 'secondary' ? palette.text : tone === 'danger' ? palette.dangerForeground : palette.accentForeground
  return (
    <Pressable accessibilityRole="button" disabled={disabled || busy} style={({ pressed }) => [styles.button, { backgroundColor, opacity: disabled || busy ? 0.45 : pressed ? 0.72 : 1 }, style as ViewStyle]} {...props}>
      {busy ? <ActivityIndicator color={color} /> : icon ? <Icon name={icon} color={color} size={17} /> : null}
      <Text style={[styles.buttonLabel, { color }]}>{label}</Text>
    </Pressable>
  )
}

export function Status({ value }: { value: string }) {
  const palette = usePalette()
  const tone = statusTone(value)
  const color = tone === 'danger' ? palette.danger : tone === 'warning' ? palette.warning : tone === 'success' ? palette.success : palette.muted
  return <View style={[styles.status, { borderColor: color }]}><View style={[styles.dot, { backgroundColor: color }]} /><Text style={[styles.statusText, { color }]}>{value.replaceAll('_', ' ')}</Text></View>
}

export function Empty({ icon, title, detail, action }: { icon: SymbolViewProps['name']; title: string; detail: string; action?: ReactNode }) {
  const palette = usePalette()
  return <View style={styles.empty}><Icon name={icon} size={34} color={palette.muted} /><Text style={[styles.emptyTitle, { color: palette.text }]}>{title}</Text><Text style={[styles.emptyDetail, { color: palette.muted }]}>{detail}</Text>{action}</View>
}

export function ErrorState({ message, retry }: { message: string; retry: () => void }) {
  return <Empty icon="exclamationmark.triangle" title="Couldn’t load this view" detail={message} action={<Button label="Try again" icon="arrow.clockwise" onPress={retry} />} />
}

export function Loading() {
  const palette = usePalette()
  return <View style={styles.loading}><ActivityIndicator color={palette.accent} /></View>
}

export function formatTime(value: string | null | undefined) {
  if (!value) return 'Not yet'
  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

export const sharedStyles = StyleSheet.create({
  page: { flex: 1 },
  listContent: { paddingHorizontal: Space.lg, paddingTop: Space.lg, paddingBottom: 120, gap: Space.md },
  title: { fontSize: 17, fontWeight: '600' },
  body: { fontSize: 15, lineHeight: 21 },
  detail: { fontSize: 13, lineHeight: 18 },
  row: { flexDirection: 'row', alignItems: 'center', gap: Space.md },
  split: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: Space.md },
  actions: { flexDirection: 'row', gap: Space.sm, flexWrap: 'wrap' },
})

const styles = StyleSheet.create({
  mark: { position: 'relative' },
  tile: { position: 'absolute', borderRadius: 2 },
  card: { borderWidth: StyleSheet.hairlineWidth, borderRadius: Radius.md, padding: Space.lg, gap: Space.md },
  sectionTitle: { marginTop: Space.md, marginBottom: Space.xs, gap: 2 },
  sectionText: { fontSize: 18, fontWeight: '700' },
  detail: { fontSize: 13, lineHeight: 18 },
  button: { minHeight: 44, borderRadius: Radius.sm, paddingHorizontal: Space.lg, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: Space.sm },
  buttonLabel: { fontSize: 15, fontWeight: '600' },
  status: { minHeight: 25, borderWidth: StyleSheet.hairlineWidth, borderRadius: 13, paddingHorizontal: 9, flexDirection: 'row', alignItems: 'center', gap: 6, alignSelf: 'flex-start' },
  statusText: { fontSize: 11, fontWeight: '700', textTransform: 'uppercase' },
  dot: { width: 6, height: 6, borderRadius: 3 },
  empty: { flex: 1, minHeight: 360, alignItems: 'center', justifyContent: 'center', padding: Space.xxl, gap: Space.md },
  emptyTitle: { fontSize: 19, fontWeight: '700', textAlign: 'center' },
  emptyDetail: { fontSize: 15, lineHeight: 21, textAlign: 'center', maxWidth: 330 },
  loading: { flex: 1, minHeight: 300, alignItems: 'center', justifyContent: 'center' },
})