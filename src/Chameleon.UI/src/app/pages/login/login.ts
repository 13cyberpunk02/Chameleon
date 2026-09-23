import {Component, inject, signal} from '@angular/core';
import {AuthService} from '../../core/auth.service';
import {ApiService} from '../../core/api.service';
import {Router} from '@angular/router';
import {FormsModule} from '@angular/forms';

@Component({
  imports: [FormsModule],
  selector: 'app-login',
  styleUrl: './login.css',
  templateUrl: './login.html',
})
export class Login {
  private auth = inject(AuthService);
  private api = inject(ApiService);
  private router = inject(Router);

  token = '';
  busy = signal(false);
  error = signal('');

  submit() {
    const t = this.token.trim();
    if (!t) { this.error.set('Введите токен'); return; }
    this.busy.set(true);
    this.error.set('');
    this.auth.login(t);
    this.api.server().subscribe({
      next: () => { this.busy.set(false); this.router.navigate(['/dashboard']); },
      error: (e) => {
        this.busy.set(false);
        this.auth.logout();
        this.error.set(e.status === 401 ? 'Неверный токен' : 'Сервер недоступен: ' + (e.message ?? e.status));
      },
    });
  }
}
