export function humanBytes(b: number): string {
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let v = b, k = 0;
  while (v >= 1024 && k < u.length - 1) { v /= 1024; k++; }
  return `${v.toFixed(v < 10 && k > 0 ? 1 : 0)} ${u[k]}`;
}

export function humanUptime(sec: number): string {
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
  if (h > 0) return `${h}ч ${m}м`;
  if (m > 0) return `${m}м ${s}с`;
  return `${s}с`;
}

export function shortKey(hex: string, n = 12): string {
  return hex.length > n ? hex.slice(0, n) + '…' : hex;
}

export function timeAgo(iso: string): string {
  const d = new Date(iso).getTime();
  const diff = Math.max(0, Math.floor((Date.now() - d) / 1000));
  if (diff < 60) return `${diff}с назад`;
  if (diff < 3600) return `${Math.floor(diff / 60)}м назад`;
  return `${Math.floor(diff / 3600)}ч назад`;
}
