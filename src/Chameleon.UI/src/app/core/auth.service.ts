import { Injectable, signal } from '@angular/core';

/**
 * Состояние входа в панель. Сама сессия - в HttpOnly-cookie, её выдаёт сервер и скрипт
 * страницы её не видит. Здесь только признак «вход выполнен», чтобы не показывать панель
 * зря; настоящую проверку делает сервер (401 возвращает на экран входа).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private static readonly KEY = 'chameleon.loggedIn';
  private static readonly LEGACY_TOKEN_KEY = 'chameleon.apiToken';
  readonly loggedIn = signal(localStorage.getItem(AuthService.KEY) === '1');

  constructor() {
    // Раньше здесь лежал сам API-токен: убираем его из браузера.
    localStorage.removeItem(AuthService.LEGACY_TOKEN_KEY);
  }

  get isAuthenticated(): boolean {
    return this.loggedIn();
  }

  markLoggedIn(): void {
    localStorage.setItem(AuthService.KEY, '1');
    this.loggedIn.set(true);
  }

  clear(): void {
    localStorage.removeItem(AuthService.KEY);
    this.loggedIn.set(false);
  }
}
