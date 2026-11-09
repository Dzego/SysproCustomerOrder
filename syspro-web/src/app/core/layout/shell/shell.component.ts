import {
  ChangeDetectionStrategy,
  ChangeDetectorRef,
  Component,
  ElementRef,
  HostListener,
  Inject,
  ViewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, skip } from 'rxjs';

import { APP_CONFIG, AppConfig } from '../../config/app-config';

/**
 * Page frame: sidebar navigation, top bar and the routed content.
 *
 * Owns the mobile navigation state. The sidebar and top bar only receive inputs
 * and raise outputs, so the state has a single owner.
 */
@Component({
  selector: 'app-shell',
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  standalone: false,
})
export class ShellComponent {
  @ViewChild('mainContent', { static: true })
  private readonly mainContent!: ElementRef<HTMLElement>;

  navOpen = false;

  constructor(
    router: Router,
    private readonly changeDetector: ChangeDetectorRef,
    @Inject(APP_CONFIG) readonly config: AppConfig,
  ) {
    // Router.events is a long-lived stream, so it must be unsubscribed when the
    // shell is destroyed. takeUntilDestroyed ties the subscription to this
    // component's DestroyRef; the async pipe does not fit because the reaction
    // is a side effect (closing the menu, moving focus), not rendered data.
    router.events
      .pipe(
        filter((event): event is NavigationEnd => event instanceof NavigationEnd),
        // The first navigation is the initial page load: focus stays where the
        // browser put it.
        skip(1),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.closeNav();
        this.focusMainContent();
      });
  }

  toggleNav(): void {
    this.navOpen = !this.navOpen;
  }

  closeNav(): void {
    if (this.navOpen) {
      this.navOpen = false;
      // The call can come from a router event, outside a template event binding,
      // so an OnPush view must be told that its state changed.
      this.changeDetector.markForCheck();
    }
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.closeNav();
  }

  skipToContent(event: Event): void {
    event.preventDefault();
    this.focusMainContent();
  }

  /** Moves focus to the new page, so keyboard and screen-reader users start there. */
  private focusMainContent(): void {
    this.mainContent.nativeElement.focus({ preventScroll: true });
  }
}
