/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {
      colors: {
        // DMARC Analyzer design system — deep ink-green + teal/mint.
        teal: {
          50: '#effaf7', 100: '#d7f4ec', 200: '#afe9db', 300: '#7eddc7', 400: '#45c7aa',
          500: '#16ad8d', 600: '#0e9481', 700: '#0c7568', 800: '#0b5d54', 900: '#0a4a44', 950: '#062f2b',
        },
        mint: { 300: '#5ff0c0', 400: '#3ae0b0', 500: '#22c996' },
        ink: { 900: '#0b1d18', 800: '#0e2620', 700: '#123029' },
        gray: {
          25: 'var(--gray-25)', 50: 'var(--gray-50)', 100: 'var(--gray-100)', 200: 'var(--gray-200)', 300: 'var(--gray-300)',
          400: 'var(--gray-400)', 500: 'var(--gray-500)', 600: 'var(--gray-600)', 700: 'var(--gray-700)', 800: 'var(--gray-800)', 900: 'var(--gray-900)',
        },
        amber: { 100: '#fdf0d5', 500: '#d97706', 600: '#b45309', 800: '#8a4406' },
        red: { 100: '#fde5ea', 600: '#dc3d5c', 800: '#a81f3d' },
        blue: { 100: '#dbeafe', 600: '#2563ab' },

        // Semantic aliases (map to CSS vars for a single source of truth).
        brand: { DEFAULT: 'var(--brand)', hover: 'var(--brand-hover)', active: 'var(--brand-active)', subtle: 'var(--brand-subtle)' },
        border: { DEFAULT: 'var(--border-default)', strong: 'var(--border-strong)' },
        surface: { page: 'var(--surface-page)', card: 'var(--surface-card)', sunken: 'var(--surface-sunken)', ink: 'var(--surface-ink)' },
        body: 'var(--text-body)',
        secondary: 'var(--text-secondary)',
        faint: 'var(--text-faint)',
        link: 'var(--link)',
      },
      fontFamily: {
        display: ['"Space Grotesk"', 'ui-sans-serif', 'system-ui', 'sans-serif'],
        body: ['"Public Sans"', 'ui-sans-serif', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'SF Mono', 'Menlo', 'monospace'],
      },
      fontSize: {
        xs: '12px', sm: '13px', base: '14px', md: '15px', lg: '18px',
        xl: '22px', '2xl': '28px', '3xl': '36px', '4xl': '48px', '5xl': '60px',
      },
      letterSpacing: { tightest: '-0.03em', tight: '-0.02em', wide: '0.06em' },
      borderRadius: { xs: '6px', sm: '8px', md: '10px', lg: '14px', xl: '18px', pill: '999px' },
      boxShadow: {
        card: 'var(--shadow-card)',
        raised: 'var(--shadow-raised)',
        overlay: 'var(--shadow-overlay)',
        'ink-panel': 'var(--shadow-ink-panel)',
      },
      transitionTimingFunction: { out: 'cubic-bezier(.2,.8,.3,1)' },
    },
  },
  plugins: [],
}
