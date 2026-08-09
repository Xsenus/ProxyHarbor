/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// В разработке API прозрачно проксируется в ASP.NET Core; в Docker всё объединяет nginx.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: './src/testSetup.ts',
    // Worker threads не требуют запуска отдельного Node-процесса для каждого pool worker
    // и стабильно стартуют под Windows/антивирусом; пустой test discovery всегда является ошибкой.
    pool: 'threads',
    passWithNoTests: false,
    // Холодный запуск jsdom на Windows может превышать стандартные 5 секунд под антивирусной нагрузкой.
    testTimeout: 15_000,
  },
  server: {
    // Разрешает безопасно открывать dev-сервер из изолированного браузера Docker Desktop.
    host: true,
    allowedHosts: ['host.docker.internal'],
    proxy: { '/api': 'http://localhost:8080', '/healthz': 'http://localhost:8080' },
  },
})
