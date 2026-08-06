import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Deliberately NO dev proxy.
//
// Vite could proxy /state and /events to the debug server and make development same-origin, which
// would make CORS vanish — in development only. Every real deployment of this page is a static
// bundle on some other origin, so a proxy would hide the exact path that matters until production.
// Dev at localhost:5173 talking to 127.0.0.1:9080 is structurally identical to that deployment, so
// the CORS preflight gets exercised on every save instead of once, in anger.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    strictPort: true,
  },
})
