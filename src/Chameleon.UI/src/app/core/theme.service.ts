import { Injectable, signal } from '@angular/core';

export type Theme = 'light' | 'dark' | 'system';

/** Управление темой: light/dark/system. Пишет data-theme на <html>, хранит выбор. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private static readonly KEY = 'chameleon.theme';
  readonly theme = signal<Theme>((localStorage.getItem(ThemeService.KEY) as Theme) || 'system');
  private media = window.matchMedia('(prefers-color-scheme: light)');

  constructor() {
    this.apply();
    this.media.addEventListener('change', () => { if (this.theme() === 'system') this.apply(); });
  }

  set(theme: Theme): void {
    localStorage.setItem(ThemeService.KEY, theme);
    this.theme.set(theme);
    this.apply();
  }

  /** Итоговая (эффективная) тема с учётом system. */
  effective(): 'light' | 'dark' {
    const t = this.theme();
    if (t === 'system') return this.media.matches ? 'light' : 'dark';
    return t;
  }

  private apply(): void {
    document.documentElement.setAttribute('data-theme', this.effective());
  }
}
