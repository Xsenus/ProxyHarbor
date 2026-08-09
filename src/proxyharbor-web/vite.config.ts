import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// В разработке API прозрачно проксируется в ASP.NET Core; в Docker всё объединяет nginx.
export default defineConfig({
  plugins: [react()],
  server: {
    // Разрешает безопасно открывать dev-сервер из изолированного браузера Docker Desktop.
    allowedHosts: ['host.docker.internal'],
    proxy: { '/api': 'http://localhost:8080', '/healthz': 'http://localhost:8080' },
  },
})
