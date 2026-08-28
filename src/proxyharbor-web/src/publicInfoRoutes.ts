export type PublicInfoKind = 'pricing'|'service'|'offer'|'privacy'|'requisites'

export const publicInfoPaths:Record<string,PublicInfoKind> = {
  '/pricing':'pricing',
  '/service':'service',
  '/offer':'offer',
  '/privacy':'privacy',
  '/requisites':'requisites',
}
