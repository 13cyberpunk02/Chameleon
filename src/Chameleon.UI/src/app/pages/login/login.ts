import {Component, inject, signal} from '@angular/core';
import {AuthService} from '../../core/auth.service';
import {ApiService} from '../../core/api.service';
import {Router} from '@angular/router';
import {FormsModule} from '@angular/forms';
import {MatProgressSpinnerModule} from '@angular/material/progress-spinner';
import {MatIconModule} from '@angular/material/icon';
import {MatButtonModule} from '@angular/material/button';
import {MatInputModule} from '@angular/material/input';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatCardModule} from '@angular/material/card';

@Component({
  imports: [FormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  selector: 'app-login',
  styleUrl: './login.scss',
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
    if (!t) {
      this.error.set('Введите токен');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.auth.login(t);
    this.api.server().subscribe({
      next: () => {
        this.busy.set(false);
        this.router.navigate(['/dashboard']);
      },
      error: (e) => {
        this.busy.set(false);
        this.auth.logout();
        this.error.set(e.status === 401 ? 'Неверный токен' : 'Сервер недоступен');
      },
    });
  }
}
