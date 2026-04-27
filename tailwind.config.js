/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./SyncTheSpire/wwwroot/**/*.{html,js}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        spire: {
          bg:           'var(--spire-bg)',
          bgSubtle:     'var(--spire-bg-subtle)',
          card:         'var(--spire-card)',
          border:       'var(--spire-border)',
          borderStrong: 'var(--spire-border-strong)',
          accent:       'var(--spire-accent)',
          accentHover:  'var(--spire-accent-hover)',
          accentSoft:   'var(--spire-accent-soft)',
          danger:       'var(--spire-danger)',
          dangerHover:  'var(--spire-danger-hover)',
          success:      'var(--spire-success)',
          warn:         'var(--spire-warn)',
          text:         'var(--spire-text)',
          muted:        'var(--spire-muted)',
        }
      }
    }
  },
  plugins: [],
}
