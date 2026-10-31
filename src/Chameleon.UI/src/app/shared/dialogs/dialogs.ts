import {Component, computed, inject, Injectable, signal} from '@angular/core';
import {FormsModule} from '@angular/forms';
import {MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef} from '@angular/material/dialog';
import {MatButtonModule} from '@angular/material/button';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatIconModule} from '@angular/material/icon';
import {MatInputModule} from '@angular/material/input';
import {MatSnackBar} from '@angular/material/snack-bar';
import qrcode from 'qrcode-generator';
import {map, Observable} from 'rxjs';
import {copyText} from '../../core/clipboard';

export interface ConfirmOptions {
  title: string;
  message: string;
  action: string;
  danger?: boolean;
}

@Component({
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>{{ data.message }}</mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Отмена</button>
      <button matButton="filled" [class.danger]="data.danger" [mat-dialog-close]="true">{{ data.action }}</button>
    </mat-dialog-actions>
  `,
  styles: `.danger { --mat-button-filled-container-color: var(--mat-sys-error); --mat-button-filled-label-text-color: var(--mat-sys-on-error); }`,
})
export class ConfirmDialog {
  data = inject<ConfirmOptions>(MAT_DIALOG_DATA);
}

export interface PromptOptions {
  title: string;
  label: string;
  action: string;
  value?: string;
  hint?: string;
  /** Текст ошибки, если значение не подходит; null - подходит. */
  validate?: (value: string) => string | null;
}

@Component({
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <mat-form-field appearance="outline" class="field">
        <mat-label>{{ data.label }}</mat-label>
        <input matInput [ngModel]="value()" (ngModelChange)="value.set($event)" (keyup.enter)="submit()" cdkFocusInitial/>
        @if (data.hint && !error()) {
          <mat-hint>{{ data.hint }}</mat-hint>
        }
      </mat-form-field>
      @if (error(); as e) {
        <p class="err">{{ e }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <!-- Пустой атрибут mat-dialog-close вернул бы '', и отмена выглядела бы как ввод пустого значения. -->
      <button matButton [mat-dialog-close]="undefined">Отмена</button>
      <button matButton="filled" [disabled]="!!error()" (click)="submit()">{{ data.action }}</button>
    </mat-dialog-actions>
  `,
  styles: `
    .field { width: 100%; margin-top: 6px; }
    .err { color: var(--mat-sys-error); margin: 0; font-size: 13px; }
  `,
})
export class PromptDialog {
  data = inject<PromptOptions>(MAT_DIALOG_DATA);
  private ref = inject(MatDialogRef<PromptDialog, string>);
  value = signal(this.data.value ?? '');
  error = computed(() => this.data.validate?.(this.value()) ?? null);

  submit(): void {
    if (!this.error()) this.ref.close(this.value());
  }
}

export interface ClientLinkData {
  name: string;
  link: string;
}

/** Ссылка нового клиента. Её нельзя получить повторно, поэтому случайный клик мимо окно не закрывает. */
@Component({
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>Клиент создан: {{ data.name || 'без имени' }}</h2>
    <mat-dialog-content>
      <p class="warn">Ссылка содержит личный ключ клиента и показывается один раз.</p>
      <img class="qr" [src]="qr" alt="QR-код ссылки"/>
      <p class="muted hint">На телефоне отсканируйте код любым сканером, скопируйте текст и нажмите
        в приложении на вкладке «Серверы» вставку из буфера.</p>
      <input class="link mono" [value]="data.link" readonly #link (focus)="link.select()"/>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button matButton (click)="copy()">
        <mat-icon>content_copy</mat-icon>
        Копировать
      </button>
      <button matButton="filled" mat-dialog-close>Готово</button>
    </mat-dialog-actions>
  `,
  styles: `
    .warn { margin-top: 0; }
    .qr { display: block; margin: 0 auto 12px; border-radius: 8px; }
    .hint { font-size: 12px; text-align: center; }
    .link { width: 100%; box-sizing: border-box; padding: 8px 10px; font-size: 12px; border: 1px solid var(--mat-sys-outline-variant); border-radius: 8px; background: var(--mat-sys-surface); color: var(--mat-sys-on-surface); }
  `,
})
export class ClientLinkDialog {
  data = inject<ClientLinkData>(MAT_DIALOG_DATA);
  private snack = inject(MatSnackBar);
  qr = linkQrDataUrl(this.data.link);

  copy(): void {
    copyText(this.data.link).then(ok =>
      this.snack.open(ok ? 'Ссылка скопирована' : 'Не удалось скопировать: выделите ссылку вручную', 'OK', {duration: 3000}));
  }
}

export function linkQrDataUrl(link: string): string {
  const qr = qrcode(0, 'M');
  qr.addData(link);
  qr.make();
  // Показываем в натуральную величину: масштабирование браузером портит модули кода.
  return qr.createDataURL(5, 20);
}

@Injectable({providedIn: 'root'})
export class Dialogs {
  private dialog = inject(MatDialog);

  confirm(options: ConfirmOptions): Observable<boolean> {
    return this.dialog.open(ConfirmDialog, {data: options, width: '420px'}).afterClosed().pipe(map(r => r === true));
  }

  prompt(options: PromptOptions): Observable<string | undefined> {
    return this.dialog.open<PromptDialog, PromptOptions, string>(PromptDialog, {data: options, width: '420px'}).afterClosed();
  }

  clientLink(data: ClientLinkData): void {
    this.dialog.open(ClientLinkDialog, {data, width: '460px', disableClose: true});
  }
}
