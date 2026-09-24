import {Component, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ApiService, LogEntry, Session, Stats} from '../../core/api.service';
import {Subscription, switchMap, timer} from 'rxjs';
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
  imports: [FormsModule, MatCardModule, MatTableModule, MatChipsModule, MatFormFieldModule, MatSelectModule, MatIconModule, BaseChartDirective],
  selector: 'app-dashboard',
  styleUrl: './dashboard.scss',
  templateUrl: './dashboard.html',
})
export class Dashboard implements OnInit, OnDestroy {
  private api = inject(ApiService);
  refresh = inject(RefreshService);
  private sub?: Subscription;

  stats = signal<Stats | null>(null);
  sessions = signal<Session[]>([]);
  events = signal<LogEntry[]>([]);
  speeds = signal<{ down: number; up: number }[]>([]);
  private prev: { to: number; from: number; t: number } | null = null;

  sessionCols = ['ip', 'client', 'carriers', 'streams', 'down', 'up', 'uptime'];
  eventCols = ['time', 'event', 'ip', 'detail'];

  bytes = humanBytes;
  uptime = humanUptime;
  key = (k: string) => shortKey(k, 12);
  ago = timeAgo;

  ngOnInit(): void {
    this.startPolling();
  }

  setInterval(ms: number): void {
    this.refresh.set(ms);
    this.startPolling();
  }

  private startPolling(): void {
    this.sub?.unsubscribe();
    const ms = this.refresh.intervalMs();
    if (ms <= 0) return;
    this.sub = timer(0, ms).pipe(switchMap(() => this.api.stats())).subscribe({
      next: (s) => {
        this.stats.set(s);
        this.pushSpeed(s);
        this.api.sessions().subscribe({next: v => this.sessions.set(v)});
        this.api.events(50).subscribe({next: v => this.events.set(v)});
      },
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

  chartData = (): ChartData<'line'> => {
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
  };

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
  }
}
