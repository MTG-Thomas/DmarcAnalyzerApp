export type ThemePreference = 'system' | 'light' | 'dark'

export const THEME_STORAGE_KEY = 'dmarc-theme'
const DARK_QUERY = '(prefers-color-scheme: dark)'

export function readThemePreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(THEME_STORAGE_KEY)
    return stored === 'light' || stored === 'dark' ? stored : 'system'
  } catch {
    return 'system'
  }
}

export function watchTheme(preference: ThemePreference): () => void {
  const media = window.matchMedia?.(DARK_QUERY)
  const apply = () => {
    document.documentElement.dataset.theme =
      preference === 'system' ? (media?.matches ? 'dark' : 'light') : preference
  }

  apply()
  if (preference !== 'system' || !media) return () => undefined

  media.addEventListener('change', apply)
  return () => media.removeEventListener('change', apply)
}

export function saveThemePreference(preference: ThemePreference) {
  try {
    localStorage.setItem(THEME_STORAGE_KEY, preference)
  } catch {
    // The selected theme still applies for this page when storage is unavailable.
  }
}
