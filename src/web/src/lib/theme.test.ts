import { afterEach, describe, expect, it, vi } from 'vitest'

import {
  readThemePreference,
  saveThemePreference,
  THEME_STORAGE_KEY,
  watchTheme,
} from '@/lib/theme'

afterEach(() => {
  localStorage.clear()
  document.documentElement.removeAttribute('data-theme')
  vi.unstubAllGlobals()
})

describe('theme preference', () => {
  it('persists a choice and follows system changes only in system mode', () => {
    let dark = false
    let onChange: (() => void) | undefined
    const media = {
      get matches() { return dark },
      addEventListener: vi.fn((_event: string, listener: () => void) => { onChange = listener }),
      removeEventListener: vi.fn(),
    }
    vi.stubGlobal('matchMedia', vi.fn(() => media))

    saveThemePreference('dark')
    expect(readThemePreference()).toBe('dark')

    const stop = watchTheme('system')
    expect(document.documentElement.dataset.theme).toBe('light')
    dark = true
    onChange?.()
    expect(document.documentElement.dataset.theme).toBe('dark')

    stop()
    expect(media.removeEventListener).toHaveBeenCalledWith('change', expect.any(Function))
  })

  it('falls back from an invalid choice without subscribing an explicit theme', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia')
    const media = {
      matches: true,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
    }
    vi.stubGlobal('matchMedia', vi.fn(() => media))

    expect(readThemePreference()).toBe('system')
    watchTheme('light')()

    expect(document.documentElement.dataset.theme).toBe('light')
    expect(media.addEventListener).not.toHaveBeenCalled()
  })
})
