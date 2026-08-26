import { fireEvent, render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it } from 'vitest'
import { I18nProvider, LanguageSwitcher, useI18n } from './i18n'

function Sample() {
  const { t } = useI18n()
  return <><LanguageSwitcher/><h1>{t('liveCatalog')}</h1></>
}

describe('ProxyHarbor localization', () => {
  beforeEach(() => localStorage.clear())

  it('switches the UI immediately and persists the selected language', () => {
    render(<I18nProvider><Sample/></I18nProvider>)
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'de' } })
    expect(screen.getByRole('heading', { name: 'Die besten im Moment' })).toBeInTheDocument()
    expect(localStorage.getItem('proxyharbor.language')).toBe('de')
    expect(document.documentElement.lang).toBe('de')
  })

  it('falls back to Russian for an unknown stored language', () => {
    localStorage.setItem('proxyharbor.language', 'es-MX')
    render(<I18nProvider><Sample/></I18nProvider>)
    expect(screen.getByRole('heading', { name: 'Лучшие прямо сейчас' })).toBeInTheDocument()
  })
})
