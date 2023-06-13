# Unreified
Unreified is a dependency injection and execution orchestrator for .NET

It is designed to help manage dependencies that will be created as a part of a longer or more complex process and to facilitate flow of a result of one execution step to the next.

## First class support for passing data between steps
No more using global variables or `IMemoryCache` to pass things between different services. Just return the thing. That's it.

```csharp

async static Task<Func<string>> LongOperation()
{
    await Task.Delay(1000);
    return () => "hello there";
}

void SayHello(Func<string> getMessage) => Console.WriteLine(getMessage());

var executor = new Executor();
await executor.Execute(Step.FromMethod(LongOperation), CancellationToken.None);
// the result of LongOperation (Func<string>) is now available in a container and will be injected to SayHello
await executor.Execute(Step.FromMethod(SayHello), CancellationToken.None);

```

## Support for asynchronous factories
Just do it. There is no excuse for "standard" containers to not support it, and yet it is always a problem. But not here:
```csharp

var executor = new Executor();
executor.RegisterTransientFactory<string>(async () => { await Task.Delay(1000); return "hello"; });

await executor.Execute(
    Step.FromMethod((string dependency) => Console.WriteLine(dependency)),
    CancellationToken.None);
```

## Automatic orchestration and dependency resolution
So one of your execution steps returns a thing, that thing is a dependency for a factory, and that factory returns something for the next step. Sounds awful to do with a "standard" container. Here it is easy:
```csharp

async static Task<object> LongOperation()
{
    await Task.Delay(1000);
    return new Func<string>(() => "hello there");
}

var executor = new Executor();
executor.AddSteps(
    Step.FromMethod(LongOperation), // the first step
    Step.FromMethod((string mes) => Console.WriteLine(mes))); // the next step

// the factory. the order of registration does not matter
executor.RegisterTransientFactory<string>((Func<string> func) => func());

// Notice that LongOperation returns Task<object>.
// The dependency for the string factory will be materialized during execution, as a result of one of the steps
// and then it will be properly registered as a delegate of type Func<string>
await executor.RunAll(maxDegreeOfParallelism: 1, CancellationToken.None);

```

## Parallel execution
If your execution steps do not depend on one another, you can execute them simultaneously

```csharp

record class Dependency1();
record class Dependency2();

Task<Dependency1> IndependentStep1() => Task.FromResult(new Dependency1());
Task<Dependency2> IndependentStep2() => Task.FromResult(new Dependency2());
string FirstStepWithDependencies(Dependency1 d1, Dependency2 d2) => "";
void SecondStepWithDependencies(string s) => {};

var executor = new Executor();
// These steps can be registered in ANY order. Here they happen to be registered in execution order,
// but that's just to make it easier to understand what was the intention of the author
executor.AddSteps(
    Step.FromMethod(IndependentStep1),
    Step.FromMethod(IndependentStep2),
    Step.FromMethod(FirstStepWithDependencies),
    Step.FromMethod(SecondStepWithDependencies)); 

await executor.RunAll(maxDegreeOfParallelism: 5, CancellationToken.None);
//            Look here, this is important    ^
```
This code will execute in parallel `IndependentStep1` and `IndependentStep2`, then it will run `FirstStepWithDependencies`, and the last thing will be `SecondStepWithDependencies`
