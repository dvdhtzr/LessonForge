import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Proxy /api to the LessonForge C# API (its default http launch profile is :5174).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5174',
        changeOrigin: true,
      },
    },
  },
})
