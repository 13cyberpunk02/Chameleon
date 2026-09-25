import {Component, inject, signal} from '@angular/core';
import {AuthService} from '../../core/auth.service';
import {Router, RouterLink, RouterLinkActive, RouterOutlet} from '@angular/router';
import {Theme, ThemeService} from '../../core/theme.service';
import {MatMenuModule} from '@angular/material/menu';
import {MatButtonModule} from '@angular/material/button';
import {MatIconModule} from '@angular/material/icon';
import {MatListModule} from '@angular/material/list';
import {MatToolbarModule} from '@angular/material/toolbar';
import {MatSidenavModule} from '@angular/material/sidenav';
import {MatTooltip} from '@angular/material/tooltip';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive,
    MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule, MatMenuModule, MatTooltip],
  selector: 'app-shell',
  styleUrl: './shell.scss',
  templateUrl: './shell.html',
})
export class Shell {
  private auth = inject(AuthService);
  private router = inject(Router);
  theme = inject(ThemeService);

  open = signal(false);

  toggle() {
    this.open.update(v => !v);
  }

  close() {
    this.open.set(false);
  }

  logoSrc() {
    return this.theme.effective() === 'light'
      ? '/chameleon-light.svg'
      : '/chameleon-dark.svg';
  }

  setTheme(t: Theme) {
    this.theme.set(t);
  }

  themeIcon() {
    return this.theme.theme() === 'light' ? 'light_mode' : this.theme.theme() === 'dark' ? 'dark_mode' : 'brightness_auto';
  }

  logout() {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
