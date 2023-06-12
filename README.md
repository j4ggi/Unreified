# Unreified
Unreified is a dependency injection and execution orchestrator for .NET

It is designed to help manage dependencies that will be created as a part of a longer or more complex process and to facilitate flow of a result of one execution step to the next.

Contrary to standard DI containers, it does not require a `.Build()` step. Dependencies, factories and execution steps can be registered at any moment.

Example:

```csharp

async static Task<Func<string>> LongOperation()
{
    await Task.Delay(1000);
    return () => "hello there";
}

void SayHello(Func<string> getMessage)
    => Console.WriteLine(getMessage());

var executor = new Executor();
await executor.Execute(Step.FromMethod(LongOperation), CancellationToken.None);
// the result of LongOperation (Func<string>) is now available in a container and will be injected to SayHello
await executor.Execute(Step.FromMethod(SayHello), CancellationToken.None);

```

You can also register everything in advance and let it run

```csharp

async static Task<object> LongOperation()
{
    await Task.Delay(1000);
    return new Func<string>(() => "hello there");
}

var executor = new Executor();
executor.AddSteps(
    Step.FromMethod(LongOperation),
    Step.FromMethod((string mes) => Console.WriteLine(mes)));

// the order of regitration does not matter
executor.RegisterTransientFactory<string>((Func<string> func) => func());

// it will print "hello there".
// Notice that LongOperation returns Task<object>.
// The dependency for the string factory will be materialized during execution, as a result of one of the steps
// and then it will be properly registered as a delegate of type Func<string>
await executor.RunAll(maxDegreeOfParallelism: 1, CancellationToken.None);

```
