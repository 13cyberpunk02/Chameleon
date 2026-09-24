import {Component, inject, OnInit, signal} from '@angular/core';
import {ApiService, ServerInfo} from '../../core/api.service';
import {humanUptime} from '../../core/format';
import {MatIconModule} from '@angular/material/icon';
import {MatChipsModule} from '@angular/material/chips';
import {MatCardModule} from '@angular/material/card';

@Component({
  imports: [MatCardModule, MatChipsModule, MatIconModule],
  selector: 'app-server',
  styleUrl: './server.scss',
  templateUrl: './server.html',
})
export class Server implements OnInit {
  private api = inject(ApiService);
  info = signal<ServerInfo | null>(null);
  uptime = humanUptime;
  ngOnInit(): void { this.api.server().subscribe({ next: v => this.info.set(v) }); }
}
