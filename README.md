[![Nuget (Generator)](https://img.shields.io/nuget/v/FactoryGenerator?style=flat-square)](https://www.nuget.org/packages/FactoryGenerator/)
[![Nuget (Attributes)](https://img.shields.io/nuget/v/FactoryGenerator.Attributes?style=flat-square)](https://www.nuget.org/packages/FactoryGenerator.Attributes/)
[![Build](https://img.shields.io/github/actions/workflow/status/westermo/FactoryGenerator/build.yml?branch=main&style=flat-square)](https://github.com/westermo/FactoryGenerator/actions)
[![License](https://img.shields.io/github/license/westermo/FactoryGenerator?style=flat-square)](https://github.com/westermo/FactoryGenerator/blob/develop/LICENSE)

FactoryGenerator is an IoC container that uses [Roslyn](https://github.com/dotnet/roslyn) to prepare a container for consumption at compile-time. Inspired by, but having little in common
with [Autofac](https://autofac.org/) beyond syntax choices.

## Features

- **Attribute-based Generation:** Simply decorate your code with attributes like ```[Inject]```,```[Singleton]```,```[Self]``` and more and your IoC container will be woven together.
- **Test-Overridability:** Need to swap out one injection for another to test something? Simply ```[Inject]``` a replacement inside your test project for a new container.

## Documentation

### Usage

FactoryGenerator consists of two seperate Nuget packages. **FactoryGenerator** and **FactoryGenerator.Attributes**, the former contains the actual Roslyn generator and the latter contains the
attributes and container interfaces which can be used to decorate code.

**FactoryGenerator:**

References this package to your top-level project, or whichever project will act as the place to "Weave together" your IoC container. If your code fits in a singular project, then just add this nuget
package to that. Typically these would be your "Console/WPF Application" and the likes, and not your classlibs.

**FactoryGenerator.Attributes:**

Reference this package from projects which add things to the IoC container via the ```[Inject]``` attribute, since it is the package that contains said attributes.

For an example regarding which projects are best server by which package, see the following:

```mermaid
  graph TD;
      App-->Dependency-A;
      App-->Dependency-B;
      Dependency-B-->Dependency-C;
      Dependency-A-->Dependency-C;
```

In the above graph, the reccomended action would be to add a reference to **FactoryGenerator.Attributes** to _Dependency C_ as that will in turn allow _Dependency A_ and _Dependency B_ to reference
the attributes therein. Additionally, a reference to **FactoryGenerator** is best added to _App_, which is where the IoC container will become available and be considered "Complete".

### Getting Started

In order to start using FactoryGenerator on a new (single .csproj) project, start by adding the nuget packages by, for example, using the dotnet CLI in your project directory:
`dotnet add package FactoryGenerator`
`dotnet add package FactoryGenerator.Attributes`

Afterwards, create an interface or two somewhere within your project, such as the following:
```csharp
namespace MyProject.Interfaces;
public inteface IWorldGreeter
{
  void SayHi();
}
public interface IGreetingProvider
{
  string Greeting { get; }
}
```
These are the contracts that we are interested in resolving from our container. 

Next, lets create some implementations, and inject them:
```csharp
using System;
using MyProject.Interfaces;
using FactoryGenerator.Attributes;

namespace MyProject.Implementations;
[Inject]
public class WorldGreeter(IGreetingProvider provider) : IWorldGreeter
{
  void SayHi() => Console.WriteLine(provider.Greeting);
}
[Inject, Singleton]
public class GreetingProvider : IGreetingProvider
{
  string Greeting { get; } = "Hello, World!"
}
```
There, now we have some functions and properties that can run, so, what's next?
Let's consider the entry point `Main` as the place where we want to use our generated container. Let's create it there and make it say hi!.
```csharp
namespace MyProject;
public class Program
{
  public static void Main()
  {
    //Create an instance of our compile-time container.
    var container = new Generated.DependencyInjectionContainer();
    //Get an instance of our greeter, in this case it will resolve to a new instance of WorldGreeter that uses a singleton instance of GreetingProvider as its' dependency.
    var greeter = container.Resolve<IWorldGreeter>();
    //Call the method the interface provides, will print "Hello, World!"
    greeter.SayHi();
  }
}
```
Of note is perhaps `Generated.DependencyInjectionContainer`, this is the Compile-time created implementation of our IoC container, it implements the interface `FactoryGenerator.IContainer`.

### Attributes

| Attribute                 |                                                                  Description                                                                   |     Requires |
|:--------------------------|:----------------------------------------------------------------------------------------------------------------------------------------------:|-------------:|
| ```Inject```              |                           Adds any interface directly listed by the type or Method-return-type to the IoC container                            |              |
| ```As<T>```               |      Explicitly inject the type which holds this attribute as the type specified, <br/>in addition to its directly implemented interfaces      | ```Inject``` |
| ```ExceptAs<T>```         |                                    Do not inject the type which holds this attribute as the type specified                                     | ```Inject``` |
| ```InheritInterfaces```   |                                              Also includes any interfaces that the type inherits                                               | ```Inject``` |
| ```Self```                |                                                     Also include the type itself directly.                                                     | ```Inject``` |
| ```Singleton```           |      Ensures that this type will be resolved exactly once,<br/>and any subsequent resolves of the same type will return the same instance      | ```Inject``` |
| ```Boolean(string key)``` | Creates a Runtime switch to decide whether this type should be the one that gets resolved<br/>(or the best fitting fallback option, otherwise) | ```Inject``` |

### Overriding

Overriding Injections, i.e if in the graph above _Dependency C_ injected an ```ISomething``` instance and that specific implementation of ```ISomething``` will not work for anything that uses _Dependency B_, then _Dependency B_ can substitute that injection by providing it's own injection of ```ISomething```. This overriding generally follows the project dependency tree, so if _Project A_ depends on _Project B_ which depends on _Project C_, A can override both B and C, but B cannot override A.

**Note**
Overriding Injections will not work if you resolve an ```IEnumerable<ISomething>```, as that will net your a collection of all ```ISomething``` that have been injected.

### Unprovided Values

What happens if there are some constructor values needed by certain injected implementations, such as command line arguments, that cannot be known at compile time? 
Well, consider the following 
```csharp
using System;
using FactoryGenerator.Attributes;

namespace Somewhere;
[Inject]
public class SomeClass(CommandLineOptions options, IInjectedInterface injected, IAnotherInjectedInterface another) : ISomeInterface
{
  /*...Methods and such...*/
}
```
Here, `CommandLineOptions` is a class created by an imaginary `Main` method at the start of the program, and not an option for compile-time injection.
Thus, if you were do have an injected class like this, you would find that the `new Generated.DependencyInjectionContainer()` looks a little different.

Specifically, the constructor for `DependencyInjectionContainer` will now look something like this:
```csharp
public DependencyInjectionContainer(CommandLineOptions options)
{
  /*...Generated Code...*/
}
```
And will thus require the user to provide the value at container-creation time.
