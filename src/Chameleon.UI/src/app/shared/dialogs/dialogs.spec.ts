import { linkQrDataUrl } from './dialogs';

describe('linkQrDataUrl', () => {
  it('fits a full client link with a private key and a long name', () => {
    const link = `chameleon://panel.example.com:443?key=${'a'.repeat(64)}&sni=www.example.com&ck=${'b'.repeat(64)}#${encodeURIComponent('Ноутбук Ивана на работе')}`;
    expect(linkQrDataUrl(link)).toMatch(/^data:image\/gif;base64,/);
  });
});
