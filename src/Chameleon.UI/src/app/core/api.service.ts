import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {environment} from '../../environments/environment';

export interface ServerInfo { publicKey: string; sni: string; allowlist: boolean; uptimeSeconds: number; }
export interface Stats { activeSessions: number; totalBytesToClient: number; totalBytesFromClient: number; totalStreams: number; }
export interface Session {
  sessionId: string; remoteIp: string; clientKey: string; startedUtc: string;
  uptimeSeconds: number; bytesToClient: number; bytesFromClient: number; carriers: number; streams: number;
}
export interface LogEntry { timeUtc: string; event: string; remoteIp: string; detail: string; }
export interface ClientAccount { publicKeyHex: string; name: string; enabled: boolean; addedUtc: string; }

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private base = environment.apiBase;

  server(): Observable<ServerInfo> { return this.http.get<ServerInfo>(`${this.base}/api/server`); }
  stats(): Observable<Stats> { return this.http.get<Stats>(`${this.base}/api/stats`); }
  sessions(): Observable<Session[]> { return this.http.get<Session[]>(`${this.base}/api/sessions`); }
  events(n = 100): Observable<LogEntry[]> { return this.http.get<LogEntry[]>(`${this.base}/api/events?n=${n}`); }

  clients(): Observable<ClientAccount[]> { return this.http.get<ClientAccount[]>(`${this.base}/api/clients`); }
  addClient(publicKey: string, name: string): Observable<any> {
    return this.http.post(`${this.base}/api/clients`, { publicKey, name });
  }
  setEnabled(key: string, enabled: boolean): Observable<any> {
    return this.http.patch(`${this.base}/api/clients/${key}`, { enabled });
  }
  removeClient(key: string): Observable<any> {
    return this.http.delete(`${this.base}/api/clients/${key}`);
  }
}
