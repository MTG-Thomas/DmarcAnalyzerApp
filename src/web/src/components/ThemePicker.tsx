import { useEffect, useState } from 'react'

import { Select } from '@/components/ui/select'
import {
  readThemePreference,
  saveThemePreference,
  watchTheme,
  type ThemePreference,
} from '@/lib/theme'

export function ThemePicker() {
  const [preference, setPreference] = useState(readThemePreference)

  useEffect(() => watchTheme(preference), [preference])

  return (
    <label className="mt-3 grid gap-1 text-xs font-medium text-secondary" htmlFor="theme-preference">
      Theme
      <Select
        id="theme-preference"
        value={preference}
        onChange={(event) => {
          const next = event.target.value as ThemePreference
          saveThemePreference(next)
          setPreference(next)
        }}
        options={[
          { value: 'system', label: 'System' },
          { value: 'light', label: 'Light' },
          { value: 'dark', label: 'Dark' },
        ]}
      />
    </label>
  )
}
