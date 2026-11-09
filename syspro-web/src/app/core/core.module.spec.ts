import { CoreModule } from './core.module';

describe('CoreModule', () => {
  it('can be created when no parent instance exists', () => {
    // Arrange / Act
    const create = () => new CoreModule(null);

    // Assert
    expect(create).not.toThrow();
  });

  it('refuses to load a second time, which would duplicate application singletons', () => {
    // Arrange
    const alreadyLoaded = new CoreModule(null);

    // Act
    const createAgain = () => new CoreModule(alreadyLoaded);

    // Assert
    expect(createAgain).toThrow('CoreModule is already loaded. Import it in AppModule only.');
  });
});
