import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import "./style.css";
import { I18nProvider } from "./i18n";
import { NotificationBridge, ToastProvider } from "./components/Toasts";
import { PrivacyControls } from "./components/PrivacyControls";
import { SeoHead } from "./SeoHead";

// StrictMode помогает обнаруживать небезопасные побочные эффекты ещё при разработке.
createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <I18nProvider>
      <ToastProvider>
        <SeoHead />
        <NotificationBridge apiBase={import.meta.env.VITE_API_URL ?? ""} />
        <App />
        <PrivacyControls />
      </ToastProvider>
    </I18nProvider>
  </StrictMode>,
);
