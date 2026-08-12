import { createContext, useContext } from 'react'

export const USER_ROLES = ['agency_admin', 'agency_analyst', 'client_viewer'] as const

export type UserRole = (typeof USER_ROLES)[number]

export type AuthUser = {
  id: string
  email: string
  displayName: string
  /**
   * One of {@link USER_ROLES}. Typed with a string fallback so an unknown role
   * from a newer server never breaks parsing — authz helpers compare against
   * the known values, so unknown roles fall back to least privilege.
   */
  role: UserRole | (string & {})
  isActive: boolean
  lastLoginAtUtc: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

// 'logged-out' is distinct from 'unauthenticated': it's only reached via an
// explicit logout, so LoginPage can skip the DisableLocalLogin auto-redirect
// there — otherwise a live IdP session gets silently re-used and logout looks
// like it does nothing. A session that merely expired still auto-redirects.
export type AuthStatus = 'loading' | 'authenticated' | 'unauthenticated' | 'logged-out'

export type AuthContextValue = {
  status: AuthStatus
  user: AuthUser | null
  login: (email: string, password: string) => Promise<void>
  loginWithPasskey: () => Promise<void>
  logout: () => Promise<void>
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used within an AuthProvider')
  }
  return context
}
