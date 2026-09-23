import {Component, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ApiService, LogEntry, Session, Stats} from '../../core/api.service';
import {interval, startWith, Subscription, switchMap} from 'rxjs';
import {humanBytes, humanUptime, shortKey, timeAgo} from '../../core/format';

@Component({
  imports: [],
  selector: 'app-dashboard',
  styleUrl: './dashboard.css',
  templateUrl: './dashboard.html',
})
export class Dashboard implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private sub?: Subscription;

  stats = signal<Stats | null>(null);
  sessions = signal<Session[]>([]);
  events = signal<LogEntry[]>([]);
  error = signal('');

  bytes = humanBytes;
  uptime = humanUptime;
  key = (k: string) => shortKey(k, 12);
  ago = timeAgo;

  ngOnInit(): void {
    this.sub = interval(3000).pipe(startWith(0), switchMap(() => this.api.stats())).subscribe({
      next: (s) => {
        this.stats.set(s);
        this.error.set('');
        this.loadTables();
      },
      error: (e) => this.error.set('Ошибка: ' + (e.message ?? e.status)),
    });
  }

  private loadTables(): void {
    this.api.sessions().subscribe({
      next: (v) => this.sessions.set(v), error: () => {
      }
    });
    this.api.events(50).subscribe({
      next: (v) => this.events.set(v), error: () => {
      }
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }
}
