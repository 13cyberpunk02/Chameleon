import {inject, Injectable} from '@angular/core';
import {MatSnackBar} from '@angular/material/snack-bar';
import {filter, Observable, Subject, switchMap} from 'rxjs';
import {Dialogs} from '../shared/dialogs/dialogs';
import {ApiService, ClientAccount} from './api.service';

const GB = 1024 ** 3;

/** Действия над клиентом с диалогом и уведомлением. Результат: завершается, когда изменение применено. */
@Injectable({providedIn: 'root'})
export class ClientActions {
  private api = inject(ApiService);
  private dialogs = inject(Dialogs);
  private snack = inject(MatSnackBar);

  displayName(c: Pick<ClientAccount, 'name' | 'publicKeyHex'>): string {
    return c.name || c.publicKeyHex.slice(0, 12);
  }

  toast(message: string): void {
    this.snack.open(message, 'OK', {duration: 3000});
  }

  kick(c: Pick<ClientAccount, 'name' | 'publicKeyHex'>): Observable<unknown> {
    return this.afterConfirm({
      title: 'Отключить клиента?',
      message: `Сессии клиента «${this.displayName(c)}» будут закрыты. Приложение клиента может подключиться снова.`,
      action: 'Отключить',
    }, () => this.api.kick(c.publicKeyHex), r => `Отключён (сессий: ${r?.closed ?? 0})`, 'Ошибка отключения');
  }

  remove(c: ClientAccount): Observable<unknown> {
    return this.afterConfirm({
      title: 'Удалить клиента?',
      message: `Клиент «${this.displayName(c)}» будет удалён, его сессии закрыты. Это нельзя отменить.`,
      action: 'Удалить',
      danger: true,
    }, () => this.api.removeClient(c.publicKeyHex), r => `Удалён (закрыто сессий: ${r?.closed ?? 0})`, 'Ошибка удаления');
  }

  rename(c: ClientAccount): Observable<unknown> {
    return this.afterPrompt(this.dialogs.prompt({
      title: 'Имя клиента',
      label: 'Имя',
      action: 'Сохранить',
      value: c.name,
      validate: v => v.trim().length > 100 ? 'Не длиннее 100 символов' : null,
    }), v => this.api.renameClient(c.publicKeyHex, v.trim()), () => 'Имя сохранено', 'Ошибка сохранения');
  }

  editLimit(c: ClientAccount): Observable<unknown> {
    const parse = (v: string) => parseFloat(v.replace(',', '.'));
    let gb = 0;
    return this.afterPrompt(this.dialogs.prompt({
      title: `Лимит для «${this.displayName(c)}»`,
      label: 'Скачивание за 30 дней, ГБ',
      hint: '0 - без лимита',
      action: 'Сохранить',
      value: c.limitBytes ? (c.limitBytes / GB).toFixed(1) : '0',
      validate: v => isNaN(parse(v)) || parse(v) < 0 ? 'Введите число не меньше нуля' : null,
    }), v => {
      gb = parse(v);
      return this.api.setLimit(c.publicKeyHex, Math.round(gb * GB));
    }, () => gb === 0 ? 'Лимит снят' : `Лимит: ${gb} ГБ`, 'Ошибка установки лимита');
  }

  private afterConfirm(options: Parameters<Dialogs['confirm']>[0], run: () => Observable<any>,
                       success: (r: any) => string, failure: string): Observable<unknown> {
    return this.notify(this.dialogs.confirm(options).pipe(filter(Boolean), switchMap(run)), success, failure);
  }

  private afterPrompt(value: Observable<string | undefined>, run: (v: string) => Observable<any>,
                      success: (r: any) => string, failure: string): Observable<unknown> {
    return this.notify(value.pipe(filter(v => v !== undefined), switchMap(run)), success, failure);
  }

  private notify(source: Observable<any>, success: (r: any) => string, failure: string): Observable<unknown> {
    const done = new Subject<unknown>();
    source.subscribe({
      next: r => {
        this.toast(success(r));
        done.next(r);
      },
      error: () => this.toast(failure),
      complete: () => done.complete(),
    });
    return done;
  }
}
