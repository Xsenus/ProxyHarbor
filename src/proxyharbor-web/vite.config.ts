/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { viteStaticCopy } from 'vite-plugin-static-copy'

// В разработке API прозрачно проксируется в ASP.NET Core; в Docker всё объединяет nginx.
export default defineConfig({
  plugins: [
    react(),
    // Флаги остаются отдельными кэшируемыми SVG и загружаются только для стран на текущей странице.
    viteStaticCopy({targets:[{src:'node_modules/flag-icons/flags/4x3/*.svg',dest:'flags'}]}),
  ],
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
