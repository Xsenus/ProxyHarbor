import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

describe('privacy preference storage failures', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.resetModules()
  })
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

  it('denies analytics when storage access throws and no explicit choice exists', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Blocked', 'SecurityError') })
    const preferences = await import('./privacyPreferences')
    expect(preferences.readAnalyticsChoice()).toBeNull()
    expect(preferences.analyticsAllowed()).toBe(false)
  })

  it('keeps explicit consent in memory when both reads and writes are blocked', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Blocked') })
    const preferences = await import('./privacyPreferences')
    const listener = vi.fn()
    window.addEventListener(preferences.privacyPreferenceChanged, listener)
    try {
      preferences.writeAnalyticsChoice('rejected', 2)
      expect(preferences.readAnalyticsChoice(2)).toBe('rejected')
      preferences.writeAnalyticsChoice('accepted', 2)
      expect(preferences.analyticsAllowed(2)).toBe(true)
      expect(preferences.analyticsAllowed(3)).toBe(false)
      expect(listener).toHaveBeenCalledTimes(2)
    } finally {
      window.removeEventListener(preferences.privacyPreferenceChanged, listener)
    }
  })

  it('does not resurrect persisted acceptance after a failed revocation write', async () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted')
    const blocked = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Full', 'QuotaExceededError') })
    const preferences = await import('./privacyPreferences')
    expect(preferences.analyticsAllowed()).toBe(true)
    preferences.writeAnalyticsChoice('rejected')
    expect(preferences.analyticsAllowed()).toBe(false)
    blocked.mockRestore()
    preferences.writeAnalyticsChoice('rejected')
    expect(localStorage.getItem('proxyharbor.analytics-consent.v1')).toBe('rejected')
    // Once persistence succeeds, subsequent storage changes are authoritative.
    localStorage.removeItem('proxyharbor.analytics-consent.v1')
    expect(preferences.readAnalyticsChoice()).toBeNull()
  })

  it.each(['globalPrivacyControl', 'doNotTrack'])('retains %s priority over in-memory acceptance', async (signal) => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Blocked') })
    const preferences = await import('./privacyPreferences')
    preferences.writeAnalyticsChoice('accepted')
    const browser = Object.create(navigator)
    Object.defineProperty(browser, signal, { value: signal === 'doNotTrack' ? '1' : true })
    vi.stubGlobal('navigator', browser)
    expect(preferences.analyticsAllowed()).toBe(false)
    expect(preferences.readAnalyticsChoice()).toBe('rejected')
  })
})
