import { Injectable, signal } from '@angular/core';

/**
 * Хранит API-токен (в localStorage). Честно: настоящая защита - на СЕРВЕРЕ (токен)
 * и в nginx (TLS/IP). Фронт лишь хранит токен и подставляет его в запросы.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private static readonly KEY = 'chameleon.apiToken';
  readonly token = signal<string | null>(localStorage.getItem(AuthService.KEY));

  get isAuthenticated(): boolean {
    return !!this.token();
  }

  login(token: string): void {
    const t = token.trim();
    localStorage.setItem(AuthService.KEY, t);
    this.token.set(t);
  }

  logout(): void {
    localStorage.removeItem(AuthService.KEY);
    this.token.set(null);
  }
}
