/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./SyncTheSpire/wwwroot/**/*.{html,js}'],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        spire: {
          bg: '#0f1117',
          card: '#1a1d27',
          border: '#2a2d3a',
          accent: '#6366f1',
          accentHover: '#818cf8',
          danger: '#ef4444',
          dangerHover: '#f87171',
          success: '#22c55e',
          warn: '#f59e0b',
          text: '#e2e8f0',
          muted: '#94a3b8',
        }
      }
    }
  },
  plugins: [],
}
