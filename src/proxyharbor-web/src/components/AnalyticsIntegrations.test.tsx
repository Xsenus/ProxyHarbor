import { act, cleanup, render } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AnalyticsIntegrations } from './AnalyticsIntegrations'
import { SiteSettingsContext } from '../siteSettingsContext'
import { defaultSiteSettings, type SiteSettings } from '../siteSettingsModel'
import { privacyPreferenceChanged, writeAnalyticsChoice } from '../privacyPreferences'

const sdk = { gtag: vi.fn(), ym: vi.fn(), VK: { Retargeting: { Init: vi.fn(), Hit: vi.fn() } } }
const scripts = () => [...document.querySelectorAll<HTMLScriptElement>('script[id^="proxyharbor-"]')]
const loaded = () => act(() => scripts().forEach(script => script.dispatchEvent(new Event('load'))))
const choose = (choice: 'accepted' | 'rejected', revision = 1) => act(() => writeAnalyticsChoice(choice, revision))
function config(): SiteSettings {
  return { ...structuredClone(defaultSiteSettings), analytics: {
    firstPartyEnabled: true,
    google: { enabled: true, identifier: 'G-TEST123' },
    yandex: { enabled: true, identifier: '123456' },
    vk: { enabled: true, identifier: 'VK-RTRG-test' },
  } }
}
function tree(settings = config()) {
  return <SiteSettingsContext.Provider value={{settings, loading:false, analyticsReady:true, refresh:async () => settings}}>
    <AnalyticsIntegrations/>
  </SiteSettingsContext.Provider>
}

beforeEach(() => {
  localStorage.clear()
  vi.clearAllMocks()
  Object.assign(window, sdk)
})
afterEach(() => {
  cleanup()
  scripts().forEach(script => script.remove())
  vi.restoreAllMocks()
  const values = window as unknown as Record<string, unknown>
  for (const key of ['gtag','ym','VK','dataLayer','ga-disable-G-TEST123','ga-disable-G-SECOND','disableYaCounter123456']) delete values[key]
})

describe('analytics consent lifecycle', () => {
  it('does not load scripts before explicit consent', () => {
    render(tree())
    expect(scripts()).toHaveLength(0)
    expect(sdk.gtag).not.toHaveBeenCalled()
    expect(sdk.ym).not.toHaveBeenCalled()
  })

  it('initializes once after load and ignores duplicate consent signals', () => {
    render(tree())
    choose('accepted')
    expect(scripts()).toHaveLength(3)
    expect(sdk.ym).not.toHaveBeenCalled()
    expect(sdk.VK.Retargeting.Hit).not.toHaveBeenCalled()
    loaded()
    act(() => window.dispatchEvent(new Event(privacyPreferenceChanged)))
    loaded()
    expect(sdk.ym).toHaveBeenCalledTimes(1)
    expect(sdk.gtag.mock.calls.filter(call => call[0] === 'config')).toHaveLength(1)
    expect(sdk.VK.Retargeting.Hit).toHaveBeenCalledTimes(1)
    expect(scripts()).toHaveLength(3)
  })

  it('cancels late SDK callbacks after revocation, including after reacceptance', () => {
    render(tree())
    choose('accepted')
    const stale = scripts()
    choose('rejected')
    choose('accepted')
    act(() => stale.forEach(script => script.dispatchEvent(new Event('load'))))
    expect(sdk.ym).not.toHaveBeenCalled()
    expect(sdk.gtag).not.toHaveBeenCalled()
    expect(sdk.VK.Retargeting.Hit).not.toHaveBeenCalled()
    loaded()
    expect(sdk.VK.Retargeting.Hit).toHaveBeenCalledTimes(1)
  })

  it('stops initialized counters on revocation and can restart after acceptance', () => {
    render(tree())
    choose('accepted')
    loaded()
    choose('rejected')
    expect(scripts()).toHaveLength(0)
    expect(sdk.ym).toHaveBeenLastCalledWith(123456, 'destruct')
    expect(sdk.gtag).toHaveBeenLastCalledWith('consent', 'update', expect.objectContaining({analytics_storage:'denied'}))
    expect((window as unknown as Record<string, unknown>)['ga-disable-G-TEST123']).toBe(true)
    choose('accepted')
    loaded()
    expect(sdk.ym.mock.calls.filter(call => call[1] === 'init')).toHaveLength(2)
  })

  it('disables old identifiers on reconfiguration and cancels obsolete loads', () => {
    const settings = config()
    const view = render(tree(settings))
    choose('accepted')
    loaded()
    const updated = config()
    updated.analytics.google.identifier = 'G-SECOND'
    view.rerender(tree(updated))
    expect((window as unknown as Record<string, unknown>)['ga-disable-G-TEST123']).toBe(true)
    expect(sdk.ym).toHaveBeenLastCalledWith(123456, 'destruct')
    const stale = scripts()
    view.rerender(tree({...updated, cookieConsentRevision:2}))
    act(() => stale.forEach(script => script.dispatchEvent(new Event('load'))))
    expect(scripts()).toHaveLength(0)
    expect(sdk.gtag.mock.calls.filter(call => call[0] === 'config')).toHaveLength(1)
  })

  it('continues stopping other SDKs if one throws during shutdown', () => {
    render(tree())
    choose('accepted')
    loaded()
    vi.spyOn(console, 'warn').mockImplementation(() => {})
    sdk.gtag.mockImplementationOnce(() => { throw new Error('SDK failed') })
    choose('rejected')
    expect(sdk.ym).toHaveBeenLastCalledWith(123456, 'destruct')
    expect(scripts()).toHaveLength(0)
  })

  it('cancels pending callbacks on unmount', () => {
    const view = render(tree())
    choose('accepted')
    const stale = scripts()
    view.unmount()
    act(() => stale.forEach(script => script.dispatchEvent(new Event('load'))))
    expect(sdk.VK.Retargeting.Hit).not.toHaveBeenCalled()
    expect(sdk.ym).not.toHaveBeenCalled()
  })

  it('stops active counters when another tab clears consent', () => {
    render(tree())
    choose('accepted')
    loaded()
    localStorage.clear()
    act(() => window.dispatchEvent(new StorageEvent('storage', {key:null})))
    expect(sdk.ym).toHaveBeenLastCalledWith(123456, 'destruct')
    expect(scripts()).toHaveLength(0)
  })
})
