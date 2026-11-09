import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot } from '@angular/router';

import { APP_TITLE, AppTitleStrategy } from './app-title-strategy';

describe('AppTitleStrategy', () => {
  let strategy: AppTitleStrategy;
  let title: Title;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [AppTitleStrategy] });
    strategy = TestBed.inject(AppTitleStrategy);
    title = TestBed.inject(Title);
  });

  it('appends the application name to the route title', () => {
    // Arrange
    jest.spyOn(strategy, 'buildTitle').mockReturnValue('Orders');

    // Act
    strategy.updateTitle({} as RouterStateSnapshot);

    // Assert
    expect(title.getTitle()).toBe(`Orders · ${APP_TITLE}`);
  });

  it('falls back to the application name when the route has no title', () => {
    // Arrange
    jest.spyOn(strategy, 'buildTitle').mockReturnValue(undefined);

    // Act
    strategy.updateTitle({} as RouterStateSnapshot);

    // Assert
    expect(title.getTitle()).toBe(APP_TITLE);
  });
});
