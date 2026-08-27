import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './style.css'
import { I18nProvider } from './i18n'
import { ToastProvider } from './components/Toasts'

// StrictMode помогает обнаруживать небезопасные побочные эффекты ещё при разработке.
createRoot(document.getElementById('root')!).render(
  <StrictMode><I18nProvider><ToastProvider><App /></ToastProvider></I18nProvider></StrictMode>,
)
