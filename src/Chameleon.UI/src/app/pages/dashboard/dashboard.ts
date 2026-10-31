import {Component, computed, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ApiService, ClientAccount, Health, LogEntry, Session, Stats} from '../../core/api.service';
import {ClientActions} from '../../core/client-actions.service';
import {MatButtonModule} from '@angular/material/button';
import {MatTooltipModule} from '@angular/material/tooltip';
import {RouterLink} from '@angular/router';
import {catchError, EMPTY, forkJoin, Subscription, switchMap, timer} from 'rxjs';
import {humanBytes, humanUptime, shortKey, timeAgo} from '../../core/format';
import {RefreshService} from '../../core/refresh.service';
import {FormsModule} from '@angular/forms';
import {MatIconModule} from '@angular/material/icon';
import {MatSelectModule} from '@angular/material/select';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatChipsModule} from '@angular/material/chips';
import {MatTableModule} from '@angular/material/table';
import {MatCardModule} from '@angular/material/card';
import {ChartConfiguration, ChartData} from 'chart.js';
import {BaseChartDirective} from 'ng2-charts';

@Component({
  imports: [FormsModule, RouterLink, MatButtonModule, MatTooltipModule, MatCardModule, MatTableModule, MatChipsModule, MatFormFieldModule, MatSelectModule, MatIconModule, BaseChartDirective],
  selector: 'app-dashboard',
  styleUrl: './dashboard.scss',
  templateUrl: './dashboard.html',
})
export class Dashboard implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private actions = inject(ClientActions);
  refresh = inject(RefreshService);
  private sub?: Subscription;
  private healthSub?: Subscription;
  private static readonly HEALTH_REFRESH_MS = 30_000;

  health = signal<Health | null>(null);
  private clients = signal<ClientAccount[]>([]);
  private clientNames = computed(() => new Map(this.clients().map(c => [c.publicKeyHex, c.name])));

  stats = signal<Stats | null>(null);
  sessions = signal<Session[]>([]);
  events = signal<LogEntry[]>([]);
  speeds = signal<{ down: number; up: number }[]>([]);
  loadError = signal(false);
  private prev: { to: number; from: number; t: number } | null = null;

  sessionCols = ['ip', 'client', 'carriers', 'streams', 'down', 'up', 'uptime', 'actions'];
  eventCols = ['time', 'event', 'ip', 'detail'];

  bytes = humanBytes;
  uptime = humanUptime;
  key = (k: string) => shortKey(k, 12);
  ago = timeAgo;

  /** Клиент есть в базе: у него есть карточка. Незнакомые возможны при выключенном allowlist. */
  isKnown = (key: string) => this.clientNames().has(key);
  clientLabel = (key: string) => this.clientNames().get(key) || (this.isKnown(key) ? 'Без имени' : 'Незнакомый');

  kick(s: Session): void {
    this.actions.kick({name: this.clientNames().get(s.clientKey) ?? '', publicKeyHex: s.clientKey}).subscribe();
  }

  ngOnInit(): void {
    this.startPolling();
    this.healthSub = timer(0, Dashboard.HEALTH_REFRESH_MS)
      .pipe(switchMap(() => this.api.health().pipe(catchError(() => EMPTY))))
      .subscribe(h => this.health.set(h));
  }

  setInterval(ms: number): void {
    this.refresh.set(ms);
    this.startPolling();
  }

  private startPolling(): void {
    this.sub?.unsubscribe();
    this.loadError.set(false);
    const ms = this.refresh.intervalMs();
    if (ms <= 0) return;
    // Ошибку ловим внутри switchMap: снаружи она завершила бы поток, и опрос остановился бы навсегда.
    this.sub = timer(0, ms).pipe(
      switchMap(() => forkJoin({
        stats: this.api.stats(),
        sessions: this.api.sessions(),
        events: this.api.events(50),
        clients: this.api.clients(),
      }).pipe(catchError(() => {
        this.loadError.set(true);
        return EMPTY;
      }))),
    ).subscribe(({stats, sessions, events, clients}) => {
      this.loadError.set(false);
      this.clients.set(clients);
      this.stats.set(stats);
      this.pushSpeed(stats);
      this.sessions.set(sessions);
      this.events.set(events);
    });
  }

  private pushSpeed(s: Stats): void {
    const now = Date.now();
    if (this.prev) {
      const dt = (now - this.prev.t) / 1000;
      if (dt > 0) this.speeds.update(a => [...a, {
        down: Math.max(0, (s.totalBytesToClient - this.prev!.to) / dt),
        up: Math.max(0, (s.totalBytesFromClient - this.prev!.from) / dt)
      }].slice(-40));
    }
    this.prev = {to: s.totalBytesToClient, from: s.totalBytesFromClient, t: now};
  }

  curDown = () => this.bytes(this.speeds().at(-1)?.down ?? 0);
  curUp = () => this.bytes(this.speeds().at(-1)?.up ?? 0);

  chartData = computed<ChartData<'line'>>(() => {
    const a = this.speeds();
    const labels = a.map(() => '');
    return {
      labels,
      datasets: [
        {
          data: a.map(x => x.down), label: '↓ клиенту', borderColor: '#3fb950',
          backgroundColor: 'rgba(63,185,80,.12)', fill: true, tension: .35, pointRadius: 0, borderWidth: 2
        },
        {
          data: a.map(x => x.up), label: '↑ от клиента', borderColor: '#58a6ff',
          backgroundColor: 'rgba(88,166,255,.10)', fill: true, tension: .35, pointRadius: 0, borderWidth: 2
        },
      ],
    };
  });

  chartOptions: ChartConfiguration<'line'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    plugins: {legend: {display: false}},
    scales: {
      x: {display: false},
      y: {
        beginAtZero: true,
        ticks: {callback: (v) => humanBytes(Number(v)) + '/с', maxTicksLimit: 4, color: '#8b949e'},
        grid: {color: 'rgba(128,128,128,.15)'},
      },
    },
  };

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
    this.healthSub?.unsubscribe();
  }
}
