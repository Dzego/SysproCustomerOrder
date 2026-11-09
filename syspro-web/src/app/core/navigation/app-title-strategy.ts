import { Injectable } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';

export const APP_TITLE = 'SYSPRO Orders';

/**
 * Builds the document title from each route's `title` property, for example
 * "Customer spending · SYSPRO Orders". Screen readers announce the title on
 * navigation, so every route declares one.
 */
@Injectable()
export class AppTitleStrategy extends TitleStrategy {
  constructor(private readonly title: Title) {
    super();
  }

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const pageTitle = this.buildTitle(snapshot);

    this.title.setTitle(pageTitle ? `${pageTitle} · ${APP_TITLE}` : APP_TITLE);
  }
}
