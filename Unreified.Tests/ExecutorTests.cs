namespace Unreified.Tests;

public class ExecutorTests
{
    [Fact]
    public async Task Step_without_dependecies_executes_with_empty_container()
    {
        var res = false;
        var toExecute = Step.FromMethod(() => res = true);

        await new Executor().Execute(toExecute, default);

        Assert.True(res);
    }

    [Fact]
    public async Task Step_with_dependecies_throws_MissingDependencyException_with_empty_container()
    {
        var res = false;
        var toExecute = Step.FromMethod((string s) => res = true);

        await Assert.ThrowsAnyAsync<MissingDependencyException>(
            async () => await new Executor().Execute(toExecute, default));

        Assert.False(res);
    }

    [Fact]
    public async Task Step_with_input_dependecies_throws_MissingDependencyException_with_empty_container()
    {
        var res = false;
        var toExecute = Step.FromMethod([Input(42)]() => res = true);

        await Assert.ThrowsAnyAsync<MissingDependencyException>(
            async () => await new Executor().Execute(toExecute, default));

        Assert.False(res);
    }

    [Fact]
    public async Task Step_with_dependecies_triggers_factory()
    {
        var res = false;
        var toExecute = Step.FromMethod((string s) => res = true);

        var executor = new Executor();
        executor.RegisterTransientFactory<string>(() => "hello");

        await executor.Execute(toExecute, default);

        Assert.True(res);
    }

    [Fact]
    public async Task Step_with_dependecies_throws_if_triggered_factory_has_missing_dependecies()
    {
        var res = false;
        var toExecute = Step.FromMethod((string s) => res = true);

        var executor = new Executor();
        executor.RegisterTransientFactory<string>((int unused) => "hello");

        await Assert.ThrowsAnyAsync<MissingDependencyException>(
            async () => await executor.Execute(toExecute, default));

        Assert.False(res);
    }

    [Fact]
    public async Task Factories_are_called_recursively()
    {
        var res = false;
        var toExecute = Step.FromMethod((string s) => res = true);

        var executor = new Executor();
        executor.RegisterTransientFactory<string>((int unused) => "hello");
        executor.RegisterTransientFactory<int>(() => 3);

        await executor.Execute(toExecute, default);

        Assert.True(res);
    }

    [Fact]
    public async Task Transient_Factories_are_called_every_time()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((string s, float f) => res = true);

        var executor = new Executor();
        executor.RegisterTransientFactory<string>(() => { counter++; return ""; });
        executor.RegisterTransientFactory<float>((string unused) => 3f);

        await executor.Execute(toExecute, default);

        Assert.True(res);
        Assert.Equal(2, counter);
    }

    [Fact]
    public async Task Scoped_Factories_are_called_once_per_single_step()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((string s, float f) => { res = true; });

        var executor = new Executor();
        executor.RegisterScopedFactory<string>(() => { counter++; return ""; });
        executor.RegisterTransientFactory<float>((string unused) => 3f);

        await executor.Execute(toExecute, default);

        Assert.True(res);
        Assert.Equal(1, counter);
    }

    [Fact]
    public async Task Scoped_Factories_are_called_once_per_every_step()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((string s, float f) => { res = true; });

        var executor = new Executor();
        executor.RegisterScopedFactory<string>(() => { counter++; return ""; });
        executor.RegisterTransientFactory<float>((string unused) => 3f);

        await executor.Execute(toExecute, default);

        Assert.True(res);
        Assert.Equal(1, counter);


        var res2 = false;
        var toExecute2 = Step.FromMethod((string s) => { res2 = true; });

        await executor.Execute(toExecute2, default);
        
        Assert.True(res2);
        Assert.Equal(2, counter);
    }

    [Fact]
    public async Task Singleton_Factories_are_called_once_if_needed()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((string s, float f) => { res = true; });

        var executor = new Executor();
        executor.RegisterSingletonFactory<string>(() => { counter++; return ""; });
        executor.RegisterTransientFactory<float>((string unused) => 3f);

        await executor.Execute(toExecute, default);

        Assert.True(res);
        Assert.Equal(1, counter);


        var res2 = false;
        var toExecute2 = Step.FromMethod((string s) => { res2 = true; });

        await executor.Execute(toExecute2, token: default);
        
        Assert.True(res2);
        Assert.Equal(1, counter);
    }

    [Fact]
    public async Task Factories_are_not_called_if_not_needed()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((float f) => { res = true; });

        var executor = new Executor();

        executor.RegisterSingletonFactory<string>(() => { counter++; return ""; });
        executor.RegisterScopedFactory<string>(() => { counter++; return ""; });
        executor.RegisterTransientFactory<string>(() => { counter++; return ""; });

        executor.RegisterTransientFactory<float>(() => 3f);

        await executor.Execute(toExecute, default);

        Assert.True(res);
        Assert.Equal(0, counter);


        var res2 = false;
        var toExecute2 = Step.FromMethod(() => { res2 = true; });

        await executor.Execute(toExecute2, token: default);

        Assert.True(res2);
        Assert.Equal(0, counter);
    }

    [Fact]
    public async Task Factory_dependency_loop_throws_DependencyLoopException()
    {
        var res = false;
        var counter = 0;
        var toExecute = Step.FromMethod((float f) => { res = true; });
        var executor = new Executor();

        executor.RegisterTransientFactory<string>((float f) => { counter++; return ""; });
        executor.RegisterTransientFactory<float>((string f) => 3f);

        await Assert.ThrowsAnyAsync<DependencyLoopException>(async () => await executor.Execute(toExecute, default));
        Assert.False(res);
    }
}
