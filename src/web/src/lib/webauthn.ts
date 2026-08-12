export type PasskeyCredential = {
  id: string
  name: string
  createdAtUtc: string
  lastUsedAtUtc: string | null
  isBackupEligible: boolean
  isBackedUp: boolean
}

export type PublicKeyRequestOptionsJson = Omit<PublicKeyCredentialRequestOptions, 'challenge' | 'allowCredentials'> & {
  challenge: string
  allowCredentials?: Array<Omit<PublicKeyCredentialDescriptor, 'id'> & { id: string }>
}

export type PublicKeyCreationOptionsJson = Omit<PublicKeyCredentialCreationOptions, 'challenge' | 'user' | 'excludeCredentials'> & {
  challenge: string
  user: Omit<PublicKeyCredentialUserEntity, 'id'> & { id: string }
  excludeCredentials?: Array<Omit<PublicKeyCredentialDescriptor, 'id'> & { id: string }>
}

export type PasskeyAssertionJson = {
  id: string
  rawId: string
  type: PublicKeyCredentialType
  response: {
    authenticatorData: string
    clientDataJSON: string
    signature: string
    userHandle: string | null
  }
  clientExtensionResults: AuthenticationExtensionsClientOutputs
}

export type PasskeyAttestationJson = {
  id: string
  rawId: string
  type: PublicKeyCredentialType
  response: {
    attestationObject: string
    clientDataJSON: string
    transports: AuthenticatorTransport[]
  }
  clientExtensionResults: AuthenticationExtensionsClientOutputs
}

export class PasskeyBrowserError extends Error {
  readonly reason: 'unsupported' | 'cancelled' | 'failed'

  constructor(reason: PasskeyBrowserError['reason']) {
    super(reason)
    this.name = 'PasskeyBrowserError'
    this.reason = reason
  }
}

const decode = (value: string): ArrayBuffer => {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/')
  const decoded = atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))
  return Uint8Array.from(decoded, (character) => character.charCodeAt(0)).buffer
}

const encode = (value: ArrayBuffer): string => {
  const bytes = new Uint8Array(value)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

const ensureSupported = () => {
  if (!window.PublicKeyCredential || !navigator.credentials) {
    throw new PasskeyBrowserError('unsupported')
  }
}

const browserFailure = (error: unknown): never => {
  if (error instanceof PasskeyBrowserError) throw error
  if (error instanceof DOMException && (error.name === 'AbortError' || error.name === 'NotAllowedError')) {
    throw new PasskeyBrowserError('cancelled')
  }
  throw new PasskeyBrowserError('failed')
}

export async function requestPasskey(options: PublicKeyRequestOptionsJson): Promise<PasskeyAssertionJson> {
  ensureSupported()
  try {
    const credential = await navigator.credentials.get({
      publicKey: {
        ...options,
        challenge: decode(options.challenge),
        allowCredentials: options.allowCredentials?.map((item) => ({ ...item, id: decode(item.id) })),
      },
    }) as PublicKeyCredential | null
    if (!credential) throw new PasskeyBrowserError('cancelled')

    const response = credential.response as AuthenticatorAssertionResponse
    return {
      id: credential.id,
      rawId: encode(credential.rawId),
      type: credential.type as PublicKeyCredentialType,
      response: {
        authenticatorData: encode(response.authenticatorData),
        clientDataJSON: encode(response.clientDataJSON),
        signature: encode(response.signature),
        userHandle: response.userHandle ? encode(response.userHandle) : null,
      },
      clientExtensionResults: credential.getClientExtensionResults(),
    }
  } catch (error) {
    return browserFailure(error)
  }
}

export async function createPasskey(options: PublicKeyCreationOptionsJson): Promise<PasskeyAttestationJson> {
  ensureSupported()
  try {
    const credential = await navigator.credentials.create({
      publicKey: {
        ...options,
        challenge: decode(options.challenge),
        user: { ...options.user, id: decode(options.user.id) },
        excludeCredentials: options.excludeCredentials?.map((item) => ({ ...item, id: decode(item.id) })),
      },
    }) as PublicKeyCredential | null
    if (!credential) throw new PasskeyBrowserError('cancelled')

    const response = credential.response as AuthenticatorAttestationResponse
    return {
      id: credential.id,
      rawId: encode(credential.rawId),
      type: credential.type as PublicKeyCredentialType,
      response: {
        attestationObject: encode(response.attestationObject),
        clientDataJSON: encode(response.clientDataJSON),
        transports: (response.getTransports?.() ?? []) as AuthenticatorTransport[],
      },
      clientExtensionResults: credential.getClientExtensionResults(),
    }
  } catch (error) {
    return browserFailure(error)
  }
}
