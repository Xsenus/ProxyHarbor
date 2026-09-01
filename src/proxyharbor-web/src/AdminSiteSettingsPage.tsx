import {
  ArrowRight,
  Gauge,
  HelpCircle,
  LockKeyhole,
  MousePointerClick,
  RefreshCw,
  ShieldCheck,
  ShieldOff,
} from "lucide-react";
import { useEffect, useState } from "react";
import { Toggle } from "./components/Toggle";
import { ToastSignal } from "./components/Toasts";
import {
  bankRequisiteFields,
  requisiteFieldCodes,
  requisiteFieldLabels,
  requiredSiteSections,
  sectionHref,
  siteSectionCodes,
  siteSectionLabels,
  type RequisiteFieldCode,
  type SiteSectionCode,
  type SiteSettings,
} from "./siteSettingsModel";
import { useSiteSettings } from "./siteSettingsContext";

const API = import.meta.env.VITE_API_URL ?? "";
type SettingsTab = "sections" | "requisites" | "cookies" | "analytics";

function isAbortError(reason: unknown) {
  return reason instanceof Error && reason.name === "AbortError";
}

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string };
    return problem.detail || problem.title || fallback;
  } catch {
    return fallback;
  }
}

function PageHeader({ busy, onSave }: { busy: boolean; onSave: () => void }) {
  return <header className="admin-page-heading">
    <nav className="admin-breadcrumb" aria-label="Положение в панели управления">
      <a href="/admin">Панель управления</a><ArrowRight aria-hidden="true" />
      <h1 id="admin-site-title">Сайт и документы</h1>
    </nav>
    <div className="admin-heading-actions">
      <button className="primary-admin-button" disabled={busy} onClick={onSave}>
        <ShieldCheck />{busy ? "Сохраняем…" : "Сохранить и опубликовать"}
      </button>
    </div>
  </header>;
}

function SettingsTabs({ value, onChange }: { value: SettingsTab; onChange: (value: SettingsTab) => void }) {
  const items: [SettingsTab, string][] = [
    ["sections", "Разделы"], ["requisites", "Реквизиты"],
    ["cookies", "Cookies"], ["analytics", "Метрики"],
  ];
  const focus = (index: number, event: React.KeyboardEvent<HTMLButtonElement>) => {
    const button = event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[index];
    if (!button) return;
    button.focus();
    onChange(items[index][0]);
  };
  return <nav className="admin-tabs" role="tablist" aria-label="Настройки публичного сайта">
    {items.map(([key, label], index) => <button
      key={key}
      id={`site-settings-tab-${key}`}
      type="button"
      role="tab"
      aria-selected={value === key}
      aria-controls={`site-settings-panel-${key}`}
      tabIndex={value === key ? 0 : -1}
      className={value === key ? "active" : ""}
      onClick={() => onChange(key)}
      onKeyDown={(event) => {
        if (!["ArrowRight", "ArrowLeft", "Home", "End"].includes(event.key)) return;
        event.preventDefault();
        if (event.key === "ArrowRight") focus((index + 1) % items.length, event);
        else if (event.key === "ArrowLeft") focus((index - 1 + items.length) % items.length, event);
        else focus(event.key === "Home" ? 0 : items.length - 1, event);
      }}
    >{label}</button>)}
  </nav>;
}

export default function AdminSiteSettingsPage() {
  const { refresh } = useSiteSettings();
  const [draft, setDraft] = useState<SiteSettings | null>(null);
  const [tab, setTab] = useState<SettingsTab>("sections");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [saved, setSaved] = useState("");

  useEffect(() => {
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(`${API}/api/v1/admin/site-settings`, {
          credentials: "include", cache: "no-store", signal: controller.signal,
        });
        if (!response.ok) throw new Error(await responseMessage(response, "Не удалось загрузить настройки сайта"));
        setDraft(await response.json() as SiteSettings);
      } catch (reason) {
        if (!isAbortError(reason)) setError(reason instanceof Error ? reason.message : "Не удалось загрузить настройки сайта");
      }
    })();
    return () => controller.abort();
  }, []);

  const updateSection = (code: SiteSectionCode, patch: Partial<SiteSettings["sections"][SiteSectionCode]>) =>
    setDraft((current) => current ? {
      ...current, sections: { ...current.sections, [code]: { ...current.sections[code], ...patch } },
    } : current);
  const updateField = (code: RequisiteFieldCode, patch: Partial<SiteSettings["requisites"]["fields"][RequisiteFieldCode]>) =>
    setDraft((current) => current ? {
      ...current,
      requisites: {
        ...current.requisites,
        fields: { ...current.requisites.fields, [code]: { ...current.requisites.fields[code], ...patch } },
      },
    } : current);
  const save = async () => {
    if (!draft || busy) return;
    setBusy(true); setError(""); setSaved("");
    try {
      const response = await fetch(`${API}/api/v1/admin/site-settings`, {
        method: "PUT", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify(draft),
      });
      if (!response.ok) throw new Error(await responseMessage(response, "Настройки сайта не сохранены"));
      setDraft(await response.json() as SiteSettings);
      await refresh();
      setSaved("Настройки опубликованы. Новая конфигурация применяется без перезапуска.");
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Настройки сайта не сохранены");
    } finally {
      setBusy(false);
    }
  };

  if (!draft) return <section className="admin-section" aria-labelledby="admin-site-title">
    <PageHeader busy={false} onSave={() => {}} />
    <div className="admin-initial-loading"><RefreshCw className="spin" /><span>Загружаем настройки…</span></div>
    <ToastSignal kind="error" message={error} />
  </section>;

  const tracker = (
    code: "yandex" | "google" | "vk", title: string, description: string,
    placeholder: string, help: string,
  ) => {
    const value = draft.analytics[code];
    return <article className="admin-card site-tracker-card" key={code}>
      <div className="site-tracker-heading"><div><span className="summary-icon"><Gauge /></span><span><b>{title}</b><small>{description}</small></span></div>
        <Toggle checked={value.enabled} onChange={(enabled) => setDraft({ ...draft, analytics: { ...draft.analytics, [code]: { ...value, enabled } } })} label={value.enabled ? "Включена" : "Выключена"} /></div>
      <label>Идентификатор<input autoComplete="off" maxLength={128} placeholder={placeholder} value={value.identifier} onChange={(event) => setDraft({ ...draft, analytics: { ...draft.analytics, [code]: { ...value, identifier: event.target.value } } })} /></label>
      <a href={help} target="_blank" rel="noreferrer"><HelpCircle />Где получить идентификатор</a>
      <p><ShieldCheck />Код загружается только после явного разрешения статистики.</p>
    </article>;
  };

  return <section className="admin-section site-settings-section" aria-labelledby="admin-site-title">
    <PageHeader busy={busy} onSave={() => void save()} />
    <ToastSignal kind="error" message={error} /><ToastSignal kind="success" message={saved} />
    <SettingsTabs value={tab} onChange={setTab} />

    {tab === "sections" && <div id="site-settings-panel-sections" role="tabpanel" aria-labelledby="site-settings-tab-sections" className="admin-card site-section-settings">
      <div className="card-heading"><div><span className="kicker">PUBLICATION</span><h2>Публичные разделы</h2><p>Страницу можно снять с публикации либо оставить доступной только по прямой ссылке. Обязательные документы нельзя отключить, но можно убрать из меню.</p></div></div>
      <div className="site-section-list">{siteSectionCodes.map((code) => {
        const value = draft.sections[code]; const required = requiredSiteSections.has(code);
        return <article key={code}><div><b>{siteSectionLabels[code]}</b><small>{sectionHref(code)}{required ? " · обязательный документ" : ""}</small></div>
          <Toggle disabled={required} checked={value.published} onChange={(published) => updateSection(code, { published, showInNavigation: published ? value.showInNavigation : false })} label={value.published ? "Опубликован" : "Скрыт"} />
          <Toggle disabled={!value.published} checked={value.showInNavigation} onChange={(showInNavigation) => updateSection(code, { showInNavigation })} label="В навигации" /></article>;
      })}</div>
    </div>}

    {tab === "requisites" && <div id="site-settings-panel-requisites" role="tabpanel" aria-labelledby="site-settings-tab-requisites" className="site-requisites-settings">
      <section className="admin-card"><div className="card-heading"><div><span className="kicker">OWNER DATA</span><h2>Карточка исполнителя</h2><p>Значения используются на странице реквизитов и в юридических документах. Переключатель управляет строкой именно на странице реквизитов.</p></div></div>
        <div className="site-copy-grid"><label>Заголовок<input maxLength={120} value={draft.requisites.introTitle} onChange={(event) => setDraft({ ...draft, requisites: { ...draft.requisites, introTitle: event.target.value } })} /></label><label className="wide">Описание<textarea maxLength={500} value={draft.requisites.introDescription} onChange={(event) => setDraft({ ...draft, requisites: { ...draft.requisites, introDescription: event.target.value } })} /></label></div>
        <div className="site-requisite-fields">{requisiteFieldCodes.filter((code) => !bankRequisiteFields.has(code)).map((code) => { const value = draft.requisites.fields[code]; return <article key={code}><label><span>{requisiteFieldLabels[code]}</span><input autoComplete="off" maxLength={500} value={value.value} onChange={(event) => updateField(code, { value: event.target.value })} /></label><Toggle checked={value.visible} onChange={(visible) => updateField(code, { visible })} label={value.visible ? "Показывать" : "Скрыто"} /></article>; })}</div>
      </section>
      <section className="admin-card"><div className="card-heading"><div><span className="kicker">BANK DETAILS</span><h2>Банковские реквизиты</h2><p>Блок по умолчанию скрыт. Даже при включённом блоке можно скрыть отдельные строки.</p></div><Toggle checked={draft.requisites.bankSectionVisible} onChange={(bankSectionVisible) => setDraft({ ...draft, requisites: { ...draft.requisites, bankSectionVisible } })} label={draft.requisites.bankSectionVisible ? "Блок опубликован" : "Блок скрыт"} /></div>
        <div className="site-requisite-fields">{requisiteFieldCodes.filter((code) => bankRequisiteFields.has(code)).map((code) => { const value = draft.requisites.fields[code]; return <article key={code}><label><span>{requisiteFieldLabels[code]}</span><input autoComplete="off" maxLength={500} value={value.value} onChange={(event) => updateField(code, { value: event.target.value })} /></label><Toggle disabled={!draft.requisites.bankSectionVisible} checked={value.visible} onChange={(visible) => updateField(code, { visible })} label={value.visible ? "Показывать" : "Скрыто"} /></article>; })}</div>
        <label className="site-note-field">Пояснение<textarea maxLength={1000} value={draft.requisites.note} onChange={(event) => setDraft({ ...draft, requisites: { ...draft.requisites, note: event.target.value } })} /></label>
      </section>
    </div>}

    {tab === "cookies" && <div id="site-settings-panel-cookies" role="tabpanel" aria-labelledby="site-settings-tab-cookies" className="admin-card site-cookie-settings">
      <div className="card-heading"><div><span className="kicker">CONSENT</span><h2>Обязательный первый выбор</h2><p>Новый посетитель не может закрыть диалог, пока не выберет только необходимые cookies либо разрешит статистику. Оферта или согласие на рекламу с этим выбором не объединяются.</p></div><span className="state-pill active">Всегда включён</span></div>
      <div className="site-cookie-required"><LockKeyhole /><div><b>Необходимые cookies</b><p>Авторизация и выбранный язык работают независимо от разрешения статистики.</p></div></div>
      <div className="site-copy-grid"><label>Заголовок<input maxLength={120} value={draft.cookies.bannerTitle} onChange={(event) => setDraft({ ...draft, cookies: { ...draft.cookies, bannerTitle: event.target.value } })} /></label><label className="wide">Текст диалога<textarea maxLength={1000} value={draft.cookies.bannerText} onChange={(event) => setDraft({ ...draft, cookies: { ...draft.cookies, bannerText: event.target.value } })} /></label></div>
      <Toggle checked={draft.cookies.showSettingsButton} onChange={(showSettingsButton) => setDraft({ ...draft, cookies: { ...draft.cookies, showSettingsButton } })} label="Показывать кнопку повторной настройки" />
      <p className="privacy-note"><ShieldCheck />При изменении текста или набора метрик редакция согласия повысится автоматически, и посетители сделают выбор заново.</p>
    </div>}

    {tab === "analytics" && <div id="site-settings-panel-analytics" role="tabpanel" aria-labelledby="site-settings-tab-analytics" className="site-analytics-settings">
      <section className="admin-card first-party-analytics"><div><span className="summary-icon"><MousePointerClick /></span><span><b>Статистика ProxyHarbor</b><small>Минимальные page codes и IP с ограниченным сроком хранения; без query и рекламных cookies.</small></span></div><Toggle checked={draft.analytics.firstPartyEnabled} onChange={(firstPartyEnabled) => setDraft({ ...draft, analytics: { ...draft.analytics, firstPartyEnabled } })} label={draft.analytics.firstPartyEnabled ? "Включена" : "Выключена"} /></section>
      <div className="site-tracker-grid">{tracker("yandex", "Яндекс Метрика", "Номер счётчика; Вебвизор принудительно выключен.", "12345678", "https://yandex.ru/support/metrica/ru/quick-start")}{tracker("google", "Google Analytics 4", "Measurement ID веб-потока.", "G-XXXXXXXXXX", "https://support.google.com/analytics/answer/9539598?hl=ru")}{tracker("vk", "VK Pixel", "Идентификатор пикселя ретаргетинга.", "VK-RTRG-…", "https://ads.vk.com/")}</div>
      <aside className="site-analytics-warning"><ShieldOff /><p>Внешние метрики могут означать трансграничную передачу и использование сторонних обработчиков. Включайте их после проверки политики, уведомлений Роскомнадзора и договорных оснований.</p></aside>
    </div>}
  </section>;
}
