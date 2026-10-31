import { AuthService } from './auth.service';

describe('AuthService', () => {
  let store: Map<string, string>;

  beforeEach(() => {
    store = new Map();
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => store.set(k, v),
      removeItem: (k: string) => store.delete(k),
    });
  });

  afterEach(() => vi.unstubAllGlobals());

  it('removes the API token left by older versions and keeps nothing secret', () => {
    store.set('chameleon.apiToken', 'secret');
    const auth = new AuthService();
    expect(store.has('chameleon.apiToken')).toBe(false);
    expect(auth.isAuthenticated).toBe(false);

    auth.markLoggedIn();
    expect(auth.isAuthenticated).toBe(true);
    expect([...store.values()]).toEqual(['1']);

    auth.clear();
    expect(auth.isAuthenticated).toBe(false);
    expect(store.size).toBe(0);
  });
});
