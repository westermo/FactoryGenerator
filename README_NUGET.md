FactoryGenerator is an IoC container that uses [Roslyn](https://github.com/dotnet/roslyn) to prepare a container for consumption at compile-time. Inspired by, but having little in common
with [Autofac](https://autofac.org/) beyond syntax choices.

## Features

- **Attribute-based Generation:** Simply decorate your code with attributes like ```[Inject]```,```[Singleton]```,```[Self]``` and more and your IoC container will be woven together.
- **Test-Overridability:** Need to swap out one injection for another to test something? Simply ```[Inject]``` a replacement inside your test project for a new container.
