using System.Collections.Generic;

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
        var toExecute = Step.FromMethod([Input(42)] () => res = true);

        await Assert.ThrowsAnyAsync<MissingDependencyException>(
            async () => await new Executor().Execute(toExecute, default));

        Assert.False(res);
    }

    [Fact]
    public async Task Step_with_input_dependecies_throws_MissingDependencyException_if_registered_singleton_is_not_the_same()
    {
        var res = false;
        var executor = new Executor();
        executor.RegisterSingleton(43);
        var toExecute = Step.FromMethod([Input(42)] () => res = true);

        await Assert.ThrowsAnyAsync<MissingDependencyException>(
            async () => await executor.Execute(toExecute, default));

        Assert.False(res);
    }

    [Fact]
    public async Task Step_with_input_dependecies_accepts_registered_singleton()
    {
        var res = false;
        var executor = new Executor();
        executor.RegisterSingleton(42);
        var toExecute = Step.FromMethod([Input(42)] () => res = true);

        await executor.Execute(toExecute, default);

        Assert.True(res);
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

    [Fact]
    public async Task Disposable_singleton_dependencies_are_not_disposed()
    {
        var disp = new Disposable();
        var toExecute = Step.FromMethod((Disposable f) => { });
        var executor = new Executor();
        executor.RegisterSingleton(disp);
        await executor.Execute(toExecute, default);

        Assert.Equal(1, disp.Result);
    }

    [Fact]
    public async Task Disposable_dependencies_from_singleton_factories_are_not_disposed()
    {
        var disp = new Disposable();
        var toExecute = Step.FromMethod((Disposable f) => { });
        var executor = new Executor();
        executor.RegisterSingletonFactory<Disposable>(() => disp);
        await executor.Execute(toExecute, default);

        Assert.Equal(1, disp.Result);
    }

    [Fact]
    public async Task Disposable_dependencies_from_scoped_factories_are_disposed()
    {
        var disp = new Disposable();
        var toExecute = Step.FromMethod((Disposable f) => { });
        var executor = new Executor();
        executor.RegisterScopedFactory<Disposable>(() => disp);
        await executor.Execute(toExecute, default);

        Assert.Equal(0, disp.Result);
    }

    [Fact]
    public async Task Disposable_dependencies_from_transient_factories_are_disposed()
    {
        var disp = new Disposable();
        var toExecute = Step.FromMethod((Disposable f) => { });
        var executor = new Executor();
        executor.RegisterTransientFactory<Disposable>(() => disp);
        await executor.Execute(toExecute, default);

        Assert.Equal(0, disp.Result);
    }

    [Fact]
    public async Task Disposable_dependencies_from_are_disposed_in_reverse_order_to_creation()
    {
        var disposables = new Stack<Disposable>();
        var toExecute = Step.FromMethod((Disposable f, int a) => { });
        var executor = new Executor();
        int counter = 0;
        executor.RegisterTransientFactory<Disposable>(() =>
        {
            var res = new Disposable();
            counter++;
            res.OnDispose += a =>
            {
                var popped = disposables.Pop();
                Assert.Equal(a, popped);
                Assert.Equal(0, popped.Result);
            };
            disposables.Push(res);

            return res;
        });
        executor.RegisterTransientFactory<int>((Disposable a) => 0);
        await executor.Execute(toExecute, default);

        Assert.Equal(2, counter);
        Assert.Empty(disposables);
    }

    [Fact]
    public async Task Orchestrated_steps_wait_for_dependencies()
    {
        var list = new List<string>();
        var executor = new Executor();
        executor.AddSteps(Step.FromMethod((Disposable f) =>
        {
            list.Add("main");
        }));
        executor.AddSteps(Step.FromMethod(async () =>
        {
            list.Add("factory");
            return new Disposable();
        }));
        await executor.RunAll(1, default);

        Assert.Collection(list,
            x => Assert.Equal("factory", x),
            x => Assert.Equal("main", x));
    }

    [Fact]
    public async Task Orchestrated_steps_wait_for_nested_dependencies()
    {
        var list = new List<string>();
        var executor = new Executor();
        executor.AddSteps(Step.FromMethod((Disposable f) => { list.Add("main"); }));
        executor.AddSteps(Step.FromMethod(() =>
        {
            list.Add("factory string");
            return "";
        }));
        executor.RegisterTransientFactory<Disposable>(async (string a) =>
        {
            list.Add("factory disp");
            return new Disposable();
        });
        await executor.RunAll(1, default);
        Assert.Collection(list,
            x => Assert.Equal("factory string", x),
            x => Assert.Equal("factory disp", x),
            x => Assert.Equal("main", x));
    }

    [Fact]
    public async Task Mutually_exclusive_steps_do_not_execute_simultaneously()
    {
        var list = new List<string>();
        var executor = new Executor();
        int counter = 0;
        var sem = new SemaphoreSlim(0);
        executor.AddSteps(Step.FromMethod(
            [MutuallyExclusive("group")] async () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref counter));
                Assert.False(await sem.WaitAsync(100));
                sem.Release();
                Interlocked.Decrement(ref counter);
            }));

        executor.AddSteps(Step.FromMethod(
            [MutuallyExclusive("group")] async () =>
            {
                sem.Release();
                Assert.Equal(1, Interlocked.Increment(ref counter));
                Assert.True(await sem.WaitAsync(100));
                Interlocked.Decrement(ref counter);
            }));
        await executor.RunAll(10, default);
    }

    [Fact]
    public async Task Output_attribute_becomes_available_as_input()
    {
        var list = new List<string>();
        var executor = new Executor();

        bool executed = false;
        executor.AddSteps(Step.FromMethod(
            [Output("test")] () =>
            {
            }));

        executor.AddSteps(Step.FromMethod(
            [Input("test")] () =>
            {
                executed = true;
            }));
        await executor.RunAll(10, default);
        Assert.True(executed);
    }

    [Fact]
    public async Task Different_mutexes_can_execute_simultaneously()
    {
        var list = new List<string>();
        var executor = new Executor();
        int counter = 0;
        var sem = new SemaphoreSlim(0);
        var sem2 = new SemaphoreSlim(0);
        executor.AddSteps(Step.FromMethod(
            [MutuallyExclusive("first")] async () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref counter));
                Assert.True(await sem2.WaitAsync(100));
                sem.Release();
                Interlocked.Decrement(ref counter);
            }));

        executor.AddSteps(Step.FromMethod(
            [MutuallyExclusive("second")] async () =>
            {
                sem2.Release();
                Assert.Equal(2, Interlocked.Increment(ref counter));
                Assert.True(await sem.WaitAsync(100));
                Interlocked.Decrement(ref counter);
            }));
        await executor.RunAll(10, default);
    }

    class Disposable : IDisposable
    {
        public int Result { get; private set; } = 1;
        public event Action<Disposable>? OnDispose;
        public void Dispose()
        {
            Result--;
            OnDispose?.Invoke(this);
        }
    }
}
