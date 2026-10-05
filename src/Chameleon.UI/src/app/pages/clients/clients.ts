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
import {MatProgressBar} from '@angular/material/progress-bar';
import {MatMenu, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {RouterLink} from '@angular/router';

@Component({
  imports: [FormsModule, MatCardModule, MatTableModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule, MatSlideToggleModule, MatSnackBarModule, MatTooltipModule, MatProgressBar, MatMenuItem, MatMenu, RouterLink, MatMenuTrigger],
  selector: 'app-clients',
  styleUrl: './clients.scss',
  templateUrl: './clients.html',
})
export class Clients implements OnInit, OnDestroy {
  private api = inject(ApiService);
  private snack = inject(MatSnackBar);

  clients = signal<ClientAccount[]>([]);
  generated = signal<{ publicKey: string; privateKey: string; name: string; link: string } | null>(null);
  newKey = '';
  newName = '';
  busy = signal(false);

  cols = ['online', 'name', 'key', 'down', 'up', 'limit', 'seen', 'enabled', 'added', 'actions'];
  key = (k: string) => shortKey(k, 20);
  date = (iso: string) => new Date(iso).toLocaleString();
  bytes = humanBytes;
  seen = (c: ClientAccount) => c.online ? 'онлайн' : (c.lastSeenUtc ? timeAgo(c.lastSeenUtc) : '-');
  pct = (c: ClientAccount) => c.limitBytes ? Math.min(100, ((c.periodDown || 0) / c.limitBytes) * 100) : 0;

  kick(c: ClientAccount): void {
    if (!confirm(`Отключить сейчас клиента ${c.name || c.publicKeyHex.slice(0, 12)}?`)) return;
    this.api.kick(c.publicKeyHex).subscribe({
      next: (r: any) => {
        this.toast(`Отключён (сессий: ${r?.closed ?? 0})`);
        this.reload();
      },
      error: () => this.toast('Ошибка отключения'),
    });
  }

  editLimit(c: ClientAccount): void {
    const curGb = c.limitBytes ? (c.limitBytes / (1024 ** 3)).toFixed(1) : '0';
    const input = prompt(`Лимit download за 30 дней для «${c.name || c.publicKeyHex.slice(0, 12)}», ГБ (0 = безлимит):`, curGb);
    if (input === null) return;
    const gb = parseFloat(input.replace(',', '.'));
    if (isNaN(gb) || gb < 0) {
      this.toast('Некорректное число');
      return;
    }
    const bytes = Math.round(gb * (1024 ** 3));
    this.api.setLimit(c.publicKeyHex, bytes).subscribe({
      next: () => {
        this.toast(gb === 0 ? 'Лимит снят' : `Лимит: ${gb} ГБ`);
        this.reload();
      },
      error: () => this.toast('Ошибка установки лимита'),
    });
  }

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

  create(): void {
    const name = prompt('Имя клиента (например, «Ноут Ивана»):', '');
    if (name === null) return;
    this.busy.set(true);
    this.api.generateClient(name.trim()).subscribe({
      next: (g) => {
        this.busy.set(false);
        this.generated.set(g);
        this.reload();
        this.toast('Клиент создан');
      },
      error: () => {
        this.busy.set(false);
        this.toast('Ошибка создания');
      },
    });
  }

  copyLink(link: string): void {
    navigator.clipboard?.writeText(link).then(() => this.toast('Ссылка скопирована'), () => {
    });
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
