import { StrictMode } from 'react';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FirstPartyAnalytics } from './FirstPartyAnalytics';
import { AnalyticsIntegrations } from './AnalyticsIntegrations';
import { PrivacyControls } from './PrivacyControls';
import { defaultSiteSettings } from '../siteSettingsModel';
import { SiteSettingsContext, useSiteSettings } from '../siteSettingsContext';
import { SiteSettingsProvider } from '../siteSettings';
import { privacyPreferenceChanged, writeAnalyticsChoice } from '../privacyPreferences';

const beacon = vi.fn(() => true);
function tree({loading = false, ready = true, revision = 1, enabled = true} = {}) {
  const settings = structuredClone(defaultSiteSettings);
  settings.cookieConsentRevision = revision;
  settings.analytics.firstPartyEnabled = enabled;
  return <SiteSettingsContext.Provider value={{settings, loading, analyticsReady:ready, refresh:async () => settings}}>
    <FirstPartyAnalytics />
  </SiteSettingsContext.Provider>;
}
function Readiness() {
  const { loading, analyticsReady } = useSiteSettings();
  return <output>{`${loading}:${analyticsReady}`}</output>;
}
const signal = () => act(() => window.dispatchEvent(new Event(privacyPreferenceChanged)));
const choose = (value:'accepted'|'rejected', revision = 1) => act(() => writeAnalyticsChoice(value, revision));

beforeEach(() => {
  localStorage.clear();
  window.history.replaceState({}, '', '/');
  beacon.mockReset().mockReturnValue(true);
  vi.stubGlobal('navigator', {sendBeacon:beacon});
});
afterEach(() => {
  cleanup();
  localStorage.clear();
  window.history.replaceState({}, '', '/');
  vi.unstubAllGlobals();
});

describe('first-party consent readiness', () => {
  it('waits for server settings and rejects consent for an obsolete revision', () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    const view = render(tree({loading:true, ready:false}));
    expect(beacon).not.toHaveBeenCalled();
    view.rerender(tree({revision:2}));
    signal();
    expect(beacon).not.toHaveBeenCalled();
    choose('accepted', 2);
    expect(beacon).toHaveBeenCalledTimes(1);
  });

  it.each([{ready:false}, {enabled:false}])('does not collect under non-authoritative or disabled settings (%j)', options => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    render(tree(options));
    signal();
    expect(beacon).not.toHaveBeenCalled();
  });

  it('deduplicates accepted visits under StrictMode and repeated consent events', () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    render(<StrictMode>{tree()}</StrictMode>);
    signal();
    choose('accepted');
    expect(beacon).toHaveBeenCalledTimes(1);
  });

  it('reacts to cross-tab consent, then rechecks revocation before navigation', () => {
    render(tree());
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    act(() => window.dispatchEvent(new StorageEvent('storage')));
    expect(beacon).toHaveBeenCalledTimes(1);
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'rejected');
    window.history.pushState({}, '', '/pricing');
    act(() => window.dispatchEvent(new PopStateEvent('popstate')));
    expect(beacon).toHaveBeenCalledTimes(1);
  });

  it('sends only the normalized pathname, never query or fragment', async () => {
    window.history.replaceState({}, '', '/account/?payment=private#secret');
    render(tree());
    choose('accepted');
    const [, payload] = (beacon.mock.calls as unknown as [string, Blob][])[0];
    const body = await new Promise<string>(resolve => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.readAsText(payload);
    });
    expect(body).toBe('{"path":"/account"}');
  });

  it('isolates browser exceptions and retries an unqueued visit only after a new event', () => {
    beacon.mockImplementationOnce(() => { throw new DOMException('Blocked'); }).mockReturnValueOnce(false);
    render(tree());
    choose('accepted');
    expect(beacon).toHaveBeenCalledTimes(1);
    signal();
    expect(beacon).toHaveBeenCalledTimes(2);
    signal();
    signal();
    expect(beacon).toHaveBeenCalledTimes(3);
  });

  it.each([{doNotTrack:'1'}, {globalPrivacyControl:true}])('honors browser privacy signals (%j)', signalValue => {
    vi.stubGlobal('navigator', {sendBeacon:beacon, ...signalValue});
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    render(tree());
    signal();
    expect(beacon).not.toHaveBeenCalled();
  });

  it('removes listeners on unmount', () => {
    const view = render(tree());
    view.unmount();
    choose('accepted');
    expect(beacon).not.toHaveBeenCalled();
  });

  it('waits through a real provider request before using the server consent revision', async () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    let respond!: (response: Response) => void;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(resolve => { respond = resolve; })));
    render(<SiteSettingsProvider><Readiness/><FirstPartyAnalytics/></SiteSettingsProvider>);
    await waitFor(() => expect(fetch).toHaveBeenCalledTimes(1));
    expect(screen.getByRole('status')).toHaveTextContent('true:false');
    expect(beacon).not.toHaveBeenCalled();
    await act(async () => respond(new Response(JSON.stringify({...defaultSiteSettings, cookieConsentRevision:2}))));
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('false:true'));
    expect(beacon).not.toHaveBeenCalled();
    choose('accepted', 2);
    expect(beacon).toHaveBeenCalledTimes(1);
  });

  it.each(['unavailable', 'incomplete', 'invalid-revision'])('keeps display fallback separate from analytics permission (%s)', async scenario => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted');
    vi.stubGlobal('fetch', vi.fn(async () => {
      if (scenario === 'unavailable') throw new Error('Network unavailable');
      const body = scenario === 'incomplete' ? {} : {...defaultSiteSettings, cookieConsentRevision:0};
      return new Response(JSON.stringify(body), {status:200});
    }));
    render(<SiteSettingsProvider><Readiness/><FirstPartyAnalytics/><AnalyticsIntegrations/><PrivacyControls/></SiteSettingsProvider>);
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('false:false'));
    signal();
    expect(beacon).not.toHaveBeenCalled();
    expect(document.querySelectorAll('script[id^="proxyharbor-"]')).toHaveLength(0);
    // An old accepted choice can reopen settings, but fallback configuration
    // cannot be used to grant a new consent for unknown analytics providers.
    act(() => screen.getByRole('button', {name:'Настройки cookies'}).click());
    expect(screen.getByRole('button', {name:'Разрешить статистику'})).toBeDisabled();
    expect(screen.getByRole('button', {name:'Только необходимые'})).toBeEnabled();
  });
});
