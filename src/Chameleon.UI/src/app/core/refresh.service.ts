import { Injectable, signal } from '@angular/core';

/** Интервал автообновления дашборда (мс). 0 = пауза. Сохраняется. */
@Injectable({ providedIn: 'root' })
export class RefreshService {
  private static readonly KEY = 'chameleon.refreshMs';
  readonly intervalMs = signal<number>(Number(localStorage.getItem(RefreshService.KEY) ?? 3000));

  readonly options = [
    { label: '1 с', value: 1000 },
    { label: '3 с', value: 3000 },
    { label: '5 с', value: 5000 },
    { label: '10 с', value: 10000 },
    { label: 'Пауза', value: 0 },
  ];

  set(ms: number): void {
    localStorage.setItem(RefreshService.KEY, String(ms));
    this.intervalMs.set(ms);
  }
}
