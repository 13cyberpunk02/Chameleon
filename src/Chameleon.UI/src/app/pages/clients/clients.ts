import {Component, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ApiService, ClientAccount} from '../../core/api.service';
import {humanBytes, shortKey, timeAgo} from '../../core/format';
import {FormsModule} from '@angular/forms';
import {MatSnackBar, MatSnackBarModule} from '@angular/material/snack-bar';
import {MatSlideToggleModule} from '@angular/material/slide-toggle';
import {MatIconModule} from '@angular/material/icon';
import {MatButtonModule} from '@angular/material/button';
import {MatCardModule} from '@angular/material/card';
import {MatTableModule} from '@angular/material/table';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatInputModule} from '@angular/material/input';
import {MatTooltipModule} from '@angular/material/tooltip';

@Component({
  imports: [FormsModule, MatCardModule, MatTableModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule, MatSlideToggleModule, MatSnackBarModule, MatTooltipModule],
  selector: 'app-clients',
  styleUrl: './clients.scss',
  templateUrl: './clients.html',
})
export class Clients implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private snack = inject(MatSnackBar);

  clients = signal<ClientAccount[]>([]);
  newKey = '';
  newName = '';
  busy = signal(false);

  cols = ['online', 'name', 'key', 'down', 'up', 'seen', 'enabled', 'added', 'actions'];
  key = (k: string) => shortKey(k, 20);
  date = (iso: string) => new Date(iso).toLocaleString();
  bytes = humanBytes;
  seen = (c: ClientAccount) => c.online ? 'онлайн' : (c.lastSeenUtc ? timeAgo(c.lastSeenUtc) : '—');

  private timer?: any;

  ngOnInit(): void {
    this.reload();
    this.timer = setInterval(() => this.reload(), 5000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private reload(): void {
    this.api.clients().subscribe({next: v => this.clients.set(v)});
  }

  private toast(m: string): void {
    this.snack.open(m, 'OK', {duration: 3000});
  }

  add(): void {
    const key = this.newKey.trim().toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(key)) {
      this.toast('Ключ должен быть 64 hex-символа');
      return;
    }
    this.busy.set(true);
    this.api.addClient(key, this.newName.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.newKey = '';
        this.newName = '';
        this.toast('Клиент добавлен');
        this.reload();
      },
      error: () => {
        this.busy.set(false);
        this.toast('Ошибка добавления');
      },
    });
  }

  toggle(c: ClientAccount, enabled: boolean): void {
    this.api.setEnabled(c.publicKeyHex, enabled).subscribe({
      next: () => {
        this.toast(enabled ? 'Включён' : 'Выключен');
        this.reload();
      },
      error: () => {
        this.toast('Ошибка');
        this.reload();
      },
    });
  }

  remove(c: ClientAccount): void {
    if (!confirm(`Удалить клиента ${c.name || c.publicKeyHex.slice(0, 12)}?`)) return;
    this.api.removeClient(c.publicKeyHex).subscribe({
      next: () => {
        this.toast('Удалён');
        this.reload();
      },
      error: () => this.toast('Ошибка удаления'),
    });
  }
}
