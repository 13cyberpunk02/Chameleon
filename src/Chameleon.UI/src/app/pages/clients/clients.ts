import {Component, inject, OnDestroy, OnInit, signal} from '@angular/core';
import {ApiService, ClientAccount} from '../../core/api.service';
import {ClientActions} from '../../core/client-actions.service';
import {humanBytes, shortKey, timeAgo} from '../../core/format';
import {Dialogs} from '../../shared/dialogs/dialogs';
import {FormsModule} from '@angular/forms';
import {MatSnackBarModule} from '@angular/material/snack-bar';
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
  private dialogs = inject(Dialogs);
  private actions = inject(ClientActions);

  clients = signal<ClientAccount[]>([]);
  newKey = '';
  newName = '';
  busy = signal(false);
  loaded = signal(false);
  loadError = signal(false);
  allowlist = signal<boolean | null>(null);

  /** Без него строки пересоздаются при каждом обновлении, и открытое меню строки закрывается. */
  trackClient = (_: number, c: ClientAccount) => c.publicKeyHex;

  cols = ['online', 'name', 'key', 'down', 'up', 'limit', 'seen', 'enabled', 'added', 'actions'];
  key = (k: string) => shortKey(k, 20);
  date = (iso: string) => new Date(iso).toLocaleString();
  bytes = humanBytes;
  seen = (c: ClientAccount) => c.online ? 'онлайн' : (c.lastSeenUtc ? timeAgo(c.lastSeenUtc) : '-');
  pct = (c: ClientAccount) => c.limitBytes ? Math.min(100, ((c.periodDown || 0) / c.limitBytes) * 100) : 0;

  kick(c: ClientAccount): void {
    this.actions.kick(c).subscribe(() => this.reload());
  }

  editLimit(c: ClientAccount): void {
    this.actions.editLimit(c).subscribe(() => this.reload());
  }

  rename(c: ClientAccount): void {
    this.actions.rename(c).subscribe(() => this.reload());
  }

  remove(c: ClientAccount): void {
    this.actions.remove(c).subscribe(() => this.reload());
  }

  private timer?: any;

  ngOnInit(): void {
    this.api.server().subscribe({next: s => this.allowlist.set(s.allowlist)});
    this.reload();
    this.timer = setInterval(() => this.reload(), 5000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearInterval(this.timer);
  }

  private reload(): void {
    this.api.clients().subscribe({
      next: v => {
        this.clients.set(v);
        this.loaded.set(true);
        this.loadError.set(false);
      },
      error: () => this.loadError.set(true),
    });
  }

  create(): void {
    this.dialogs.prompt({
      title: 'Новый клиент',
      label: 'Имя',
      hint: 'Например, «Ноутбук Ивана». Сервер создаст ключ и готовую ссылку.',
      action: 'Создать',
      validate: v => v.trim().length > 100 ? 'Не длиннее 100 символов' : null,
    }).subscribe(name => {
      if (name === undefined) return;
      this.busy.set(true);
      this.api.generateClient(name.trim()).subscribe({
        next: (g) => {
          this.busy.set(false);
          this.dialogs.clientLink({name: g.name, link: g.link});
          this.reload();
        },
        error: () => {
          this.busy.set(false);
          this.actions.toast('Ошибка создания');
        },
      });
    });
  }

  add(): void {
    const key = this.newKey.trim().toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(key)) {
      this.actions.toast('Ключ должен быть 64 hex-символа');
      return;
    }
    this.busy.set(true);
    this.api.addClient(key, this.newName.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.newKey = '';
        this.newName = '';
        this.actions.toast('Клиент добавлен');
        this.reload();
      },
      error: () => {
        this.busy.set(false);
        this.actions.toast('Ошибка добавления');
      },
    });
  }

  toggle(c: ClientAccount, enabled: boolean): void {
    this.api.setEnabled(c.publicKeyHex, enabled).subscribe({
      next: (r: any) => {
        this.actions.toast(enabled ? 'Включён' : `Выключен (закрыто сессий: ${r?.closed ?? 0})`);
        this.reload();
      },
      error: () => {
        this.actions.toast('Ошибка');
        this.reload();
      },
    });
  }
}
