import {Component, computed, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ActivatedRoute, RouterLink} from '@angular/router';
import {FormsModule} from '@angular/forms';
import {Subscription} from 'rxjs';
import {ChartConfiguration, ChartData} from 'chart.js';
import {BaseChartDirective} from 'ng2-charts';
import {MatButtonModule} from '@angular/material/button';
import {MatCardModule} from '@angular/material/card';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatIconModule} from '@angular/material/icon';
import {MatSelectModule} from '@angular/material/select';
import {ApiService, HistoryPoint} from '../../core/api.service';
import {fillDays, humanBytes, shortKey} from '../../core/format';

@Component({
  imports: [FormsModule, RouterLink, BaseChartDirective, MatButtonModule, MatCardModule, MatFormFieldModule,
    MatIconModule, MatSelectModule],
  selector: 'app-history',
  styleUrl: './history.scss',
  templateUrl: './history.html',
})
export class History implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private sub?: Subscription;

  readonly key = inject(ActivatedRoute).snapshot.paramMap.get('key') ?? '';
  readonly periods = [7, 30, 90];

  name = signal('');
  days = signal(30);
  points = signal<HistoryPoint[] | null>(null);
  loadError = signal(false);

  bytes = humanBytes;
  shortKey = shortKey;

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
    this.api.clients().subscribe({
      next: list => this.name.set(list.find(c => c.publicKeyHex === this.key)?.name ?? ''),
    });
    this.load();
  }

  setDays(days: number): void {
    this.days.set(days);
    this.load();
  }

  load(): void {
    this.sub?.unsubscribe();
    this.loadError.set(false);
    this.sub = this.api.clientHistory(this.key, this.days()).subscribe({
      next: v => this.points.set(v),
      error: () => this.loadError.set(true),
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }
}
