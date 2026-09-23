import {Component, inject, OnInit, signal} from '@angular/core';
import {ApiService, ClientAccount} from '../../core/api.service';
import {shortKey} from '../../core/format';
import {FormsModule} from '@angular/forms';

@Component({
  imports: [FormsModule],
  selector: 'app-clients',
  styleUrl: './clients.css',
  templateUrl: './clients.html',
})
export class Clients implements OnInit {
  private api = inject(ApiService);

  clients = signal<ClientAccount[]>([]);
  newKey = '';
  newName = '';
  busy = signal(false);
  message = signal('');
  isError = signal(false);

  key = (k: string) => shortKey(k, 20);
  date = (iso: string) => new Date(iso).toLocaleString();

  ngOnInit(): void {
    this.reload();
  }

  private reload(): void {
    this.api.clients().subscribe({
      next: (v) => this.clients.set(v),
      error: (e) => this.flash('Ошибка загрузки: ' + (e.message ?? e.status), true),
    });
  }

  private flash(msg: string, err = false): void {
    this.message.set(msg);
    this.isError.set(err);
    setTimeout(() => this.message.set(''), 4000);
  }

  add(): void {
    const key = this.newKey.trim().toLowerCase();
    if (!/^[0-9a-f]{64}$/.test(key)) {
      this.flash('Ключ должен быть 64 hex-символа', true);
      return;
    }
    this.busy.set(true);
    this.api.addClient(key, this.newName.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.newKey = '';
        this.newName = '';
        this.flash('Клиент добавлен');
        this.reload();
      },
      error: (e) => {
        this.busy.set(false);
        this.flash('Ошибка: ' + (e.message ?? e.status), true);
      },
    });
  }

  toggle(c: ClientAccount): void {
    this.api.setEnabled(c.publicKeyHex, !c.enabled).subscribe({
      next: () => {
        this.flash(c.enabled ? 'Выключен' : 'Включён');
        this.reload();
      },
      error: (e) => this.flash('Ошибка: ' + (e.message ?? e.status), true),
    });
  }

  remove(c: ClientAccount): void {
    if (!confirm(`Удалить клиента ${c.name || c.publicKeyHex.slice(0, 12)}?`)) return;
    this.api.removeClient(c.publicKeyHex).subscribe({
      next: () => {
        this.flash('Удалён');
        this.reload();
      },
      error: (e) => this.flash('Ошибка: ' + (e.message ?? e.status), true),
    });
  }
}
