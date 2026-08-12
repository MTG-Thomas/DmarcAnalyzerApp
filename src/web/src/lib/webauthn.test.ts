import { beforeEach, describe, expect, it, vi } from 'vitest'

import {
  createPasskey,
  requestPasskey,
  type PublicKeyCreationOptionsJson,
  type PublicKeyRequestOptionsJson,
} from '@/lib/webauthn'

const options: PublicKeyRequestOptionsJson = {
  challenge: 'AQID',
  rpId: 'dmarc.midtowntg.com',
  userVerification: 'required',
}

describe('WebAuthn browser adapter', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('reports an unsupported browser without starting a ceremony', async () => {
    vi.stubGlobal('PublicKeyCredential', undefined)
    await expect(requestPasskey(options)).rejects.toMatchObject({ reason: 'unsupported' })
  })

  it('maps an aborted ceremony to a recoverable cancellation', async () => {
    vi.stubGlobal('PublicKeyCredential', class {})
    Object.defineProperty(navigator, 'credentials', {
      configurable: true,
      value: { get: vi.fn().mockRejectedValue(new DOMException('cancelled', 'AbortError')) },
    })

    await expect(requestPasskey(options)).rejects.toMatchObject({ reason: 'cancelled' })
  })

  it('decodes options and serializes an assertion as base64url', async () => {
    vi.stubGlobal('PublicKeyCredential', class {})
    const get = vi.fn().mockResolvedValue({
      id: 'credential-id',
      rawId: Uint8Array.from([1, 2, 3]).buffer,
      type: 'public-key',
      response: {
        authenticatorData: Uint8Array.from([4]).buffer,
        clientDataJSON: Uint8Array.from([5]).buffer,
        signature: Uint8Array.from([6]).buffer,
        userHandle: null,
      },
      getClientExtensionResults: () => ({}),
    })
    Object.defineProperty(navigator, 'credentials', { configurable: true, value: { get } })

    const result = await requestPasskey(options)

    expect(new Uint8Array(get.mock.calls[0][0].publicKey.challenge)).toEqual(Uint8Array.from([1, 2, 3]))
    expect(result).toEqual(expect.objectContaining({
      id: 'credential-id',
      rawId: 'AQID',
      response: expect.objectContaining({ authenticatorData: 'BA', clientDataJSON: 'BQ', signature: 'Bg' }),
    }))
  })

  it('decodes registration options and serializes attestation transports', async () => {
    vi.stubGlobal('PublicKeyCredential', class {})
    const create = vi.fn().mockResolvedValue({
      id: 'credential-id',
      rawId: Uint8Array.from([1, 2, 3]).buffer,
      type: 'public-key',
      response: {
        attestationObject: Uint8Array.from([4]).buffer,
        clientDataJSON: Uint8Array.from([5]).buffer,
        getTransports: () => ['internal'],
      },
      getClientExtensionResults: () => ({}),
    })
    Object.defineProperty(navigator, 'credentials', { configurable: true, value: { create } })
    const creation: PublicKeyCreationOptionsJson = {
      challenge: 'AQID',
      rp: { name: 'DMARC Analyzer', id: 'dmarc.midtowntg.com' },
      user: { id: 'BA', name: 'operator', displayName: 'Operator' },
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
    }

    const result = await createPasskey(creation)

    expect(new Uint8Array(create.mock.calls[0][0].publicKey.challenge)).toEqual(Uint8Array.from([1, 2, 3]))
    expect(new Uint8Array(create.mock.calls[0][0].publicKey.user.id)).toEqual(Uint8Array.from([4]))
    expect(result.response).toEqual({ attestationObject: 'BA', clientDataJSON: 'BQ', transports: ['internal'] })
  })
})
