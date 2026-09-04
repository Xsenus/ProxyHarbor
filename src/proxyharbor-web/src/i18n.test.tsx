import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { I18nProvider, LanguageSwitcher, currentLocale, useI18n } from './i18n'

afterEach(() => { cleanup(); vi.restoreAllMocks() })

function Sample() {
  const { t } = useI18n()
  return <><LanguageSwitcher/><h1>{t('liveCatalog')}</h1></>
}

describe('ProxyHarbor localization', () => {
  beforeEach(() => localStorage.clear())

  it('switches the UI immediately and persists the selected language', () => {
    render(<I18nProvider><Sample/></I18nProvider>)
    const language = screen.getByRole('button', { name: /Язык|Language/ })
    fireEvent.click(language)
    expect(screen.getByRole('listbox', { name: /Язык|Language/ })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('option', { name: 'Deutsch' }))
    expect(screen.getByRole('heading', { name: 'Die besten im Moment' })).toBeInTheDocument()
    expect(localStorage.getItem('proxyharbor.language')).toBe('de')
    expect(document.documentElement.lang).toBe('de')
    expect(language).toHaveTextContent('Deutsch')
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
  })

  it('falls back to Russian for an unknown stored language', () => {
    localStorage.setItem('proxyharbor.language', 'es-MX')
    render(<I18nProvider><Sample/></I18nProvider>)
    expect(screen.getByRole('heading', { name: 'Лучшие прямо сейчас' })).toBeInTheDocument()
  })
})

function LanguageProbe() {
  const { language, setLanguage } = useI18n()
  return <button onClick={() => setLanguage('de')}>{language}</button>
}

it('renders and changes language with storage and cookies blocked', () => {
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Denied') })
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Denied') })
  vi.spyOn(document, 'cookie', 'set').mockImplementation(() => { throw new Error('Denied') })
  render(<I18nProvider><LanguageProbe/></I18nProvider>)
  fireEvent.click(screen.getByRole('button'))
  expect(screen.getByRole('button', {name:'de'})).toBeVisible()
  expect(document.documentElement.lang).toBe('de')
  expect(currentLocale()).toBe('de-DE')
})
