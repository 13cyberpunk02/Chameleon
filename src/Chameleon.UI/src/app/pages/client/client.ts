import {Component, computed, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ActivatedRoute, Router, RouterLink} from '@angular/router';
import {FormsModule} from '@angular/forms';
import {catchError, EMPTY, forkJoin, Subscription, switchMap, timer} from 'rxjs';
import {ChartConfiguration, ChartData} from 'chart.js';
import {BaseChartDirective} from 'ng2-charts';
import {MatButtonModule} from '@angular/material/button';
import {MatCardModule} from '@angular/material/card';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatIconModule} from '@angular/material/icon';
import {MatProgressBar} from '@angular/material/progress-bar';
import {MatSelectModule} from '@angular/material/select';
import {MatTableModule} from '@angular/material/table';
import {ApiService, ClientAccount, HistoryPoint, Session} from '../../core/api.service';
import {ClientActions} from '../../core/client-actions.service';
import {copyText} from '../../core/clipboard';
import {fillDays, humanBytes, humanUptime, timeAgo} from '../../core/format';

const PERIOD_DAYS = 30;
const REFRESH_MS = 5000;

@Component({
  imports: [FormsModule, RouterLink, BaseChartDirective, MatButtonModule, MatCardModule, MatFormFieldModule,
    MatIconModule, MatProgressBar, MatSelectModule, MatTableModule],
  selector: 'app-client',
  styleUrl: './client.scss',
  templateUrl: './client.html',
})
export class Client implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private actions = inject(ClientActions);
  private router = inject(Router);
  private historySub?: Subscription;
  private refreshSub?: Subscription;

  readonly key = (inject(ActivatedRoute).snapshot.paramMap.get('key') ?? '').toLowerCase();
  readonly periods = [7, 30, 90];
  readonly sessionCols = ['ip', 'carriers', 'streams', 'down', 'up', 'uptime'];

  client = signal<ClientAccount | null>(null);
  sessions = signal<Session[]>([]);
  loaded = signal(false);
  loadError = signal(false);

  days = signal(30);
  points = signal<HistoryPoint[] | null>(null);
  historyError = signal(false);

  bytes = humanBytes;
  uptime = humanUptime;
  ago = timeAgo;

  limitPct = computed(() => {
    const c = this.client();
    return c?.limitBytes ? Math.min(100, ((c.periodDown ?? 0) / c.limitBytes) * 100) : 0;
  });

  periodEnd = computed(() => {
    const start = this.client()?.periodStart;
    if (!start) return null;
    return new Date(new Date(start).getTime() + PERIOD_DAYS * 86_400_000).toLocaleDateString();
  });

  totalDown = computed(() => (this.points() ?? []).reduce((sum, p) => sum + p.down, 0));
  totalUp = computed(() => (this.points() ?? []).reduce((sum, p) => sum + p.up, 0));

  chartData = computed<ChartData<'bar'>>(() => {
    const filled = fillDays(this.points() ?? [], this.days());
    return {
      labels: filled.map(p => `${p.day.slice(8, 10)}.${p.day.slice(5, 7)}`),
      datasets: [
        {data: filled.map(p => p.down), label: '↓ клиенту', backgroundColor: '#3fb950'},
        {data: filled.map(p => p.up), label: '↑ от клиента', backgroundColor: '#58a6ff'},
      ],
    };
  });

  chartOptions: ChartConfiguration<'bar'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    plugins: {
      legend: {display: false},
      tooltip: {callbacks: {label: (c) => `${c.dataset.label}: ${humanBytes(Number(c.parsed.y))}`}},
    },
    scales: {
      x: {stacked: true, ticks: {color: '#8b949e', maxTicksLimit: 10}, grid: {display: false}},
      y: {
        stacked: true,
        beginAtZero: true,
        ticks: {callback: (v) => humanBytes(Number(v)), maxTicksLimit: 4, color: '#8b949e'},
        grid: {color: 'rgba(128,128,128,.15)'},
      },
    },
  };

  ngOnInit(): void {
    this.refreshSub = timer(0, REFRESH_MS).pipe(
      switchMap(() => forkJoin({clients: this.api.clients(), sessions: this.api.sessions()}).pipe(
        catchError(() => {
          this.loadError.set(true);
          return EMPTY;
        }))),
    ).subscribe(({clients, sessions}) => this.apply(clients, sessions));
    this.loadHistory();
  }

  private apply(clients: ClientAccount[], sessions: Session[]): void {
    this.client.set(clients.find(c => c.publicKeyHex === this.key) ?? null);
    this.sessions.set(sessions.filter(s => s.clientKey.toLowerCase() === this.key));
    this.loaded.set(true);
    this.loadError.set(false);
  }

  private reload(): void {
    forkJoin({clients: this.api.clients(), sessions: this.api.sessions()})
      .subscribe({next: ({clients, sessions}) => this.apply(clients, sessions), error: () => this.loadError.set(true)});
  }

  setDays(days: number): void {
    this.days.set(days);
    this.loadHistory();
  }

  loadHistory(): void {
    this.historySub?.unsubscribe();
    this.historyError.set(false);
    this.historySub = this.api.clientHistory(this.key, this.days()).subscribe({
      next: v => this.points.set(v),
      error: () => this.historyError.set(true),
    });
  }

  rename(c: ClientAccount): void {
    this.actions.rename(c).subscribe(() => this.reload());
  }

  editLimit(c: ClientAccount): void {
    this.actions.editLimit(c).subscribe(() => this.reload());
  }

  kick(c: ClientAccount): void {
    this.actions.kick(c).subscribe(() => this.reload());
  }

  remove(c: ClientAccount): void {
    this.actions.remove(c).subscribe(() => this.router.navigate(['/clients']));
  }

  setEnabled(c: ClientAccount, enabled: boolean): void {
    this.api.setEnabled(c.publicKeyHex, enabled).subscribe({
      next: (r: any) => {
        this.actions.toast(enabled ? 'Включён' : `Выключен (закрыто сессий: ${r?.closed ?? 0})`);
        this.reload();
      },
      error: () => this.actions.toast('Ошибка'),
    });
  }

  copyKey(): void {
    copyText(this.key).then(ok => this.actions.toast(ok ? 'Ключ скопирован' : 'Не удалось скопировать'));
  }

  ngOnDestroy(): void {
    this.refreshSub?.unsubscribe();
    this.historySub?.unsubscribe();
  }
}
