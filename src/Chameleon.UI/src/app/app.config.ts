import {ApplicationConfig, provideBrowserGlobalErrorListeners, provideZoneChangeDetection} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import {provideHttpClient, withInterceptors} from '@angular/common/http';
import {authInterceptor} from './core/auth.interceptor';
import {provideAnimationsAsync} from '@angular/platform-browser/animations/async';
import {provideCharts, withDefaultRegisterables} from 'ng2-charts';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient((withInterceptors([authInterceptor]))),
    provideCharts(withDefaultRegisterables()),
  ]
};
