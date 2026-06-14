import { Moon, Sun } from 'lucide-react'
import { Button } from '../ui/button'
import { useTheme } from './theme-provider'

export function ThemeToggle() {
  const { theme, toggleTheme } = useTheme()
  const goingDark = theme !== 'dark'
  return (
    <Button
      variant="ghost"
      size="icon"
      onClick={toggleTheme}
      aria-label={goingDark ? 'Switch to dark theme' : 'Switch to light theme'}
      title="Toggle theme"
    >
      {theme === 'dark' ? <Sun className="h-4 w-4" /> : <Moon className="h-4 w-4" />}
    </Button>
  )
}
