import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './style.css'

// StrictMode помогает обнаруживать небезопасные побочные эффекты ещё при разработке.
createRoot(document.getElementById('root')!).render(
  <StrictMode><App /></StrictMode>,
)
