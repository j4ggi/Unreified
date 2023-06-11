using static Unreified.Step;

namespace Unreified;
public class Executor
{
    public Executor(Behavior behavior = Behavior.AllowExplicitOverwrites)
    {
        Behavior = behavior;
    }

    public Behavior Behavior { get; }

    public async Task RunAll(byte maxDegreeOfParallelism, CancellationToken token)
    {
        while (steps.Count > 0)
        {
            using (var available = await GetAvailableSteps(steps))
            using (var toRun = PooledList<Step>.Get())
            {
                foreach (var step in available.List)
                {
                    if (executing.Count == maxDegreeOfParallelism)
                        break;

                    if (!IsLocked(step))
                    {
                        toRun.List.Add(step);
                        executing.Add((step, Execute(step, false, token)));
                    }
                }

                if (executing.Count == 0)
                    throw new LeftOverStepException("Could not execute all steps. Make sure all dependencies can be resolved");

                foreach (var step in toRun.List)
                    _ = steps.Remove(step);
            }

            _ = await Task.WhenAny(executing.Select(x => x.Task));
            using var completed = ToListFromPool(executing.Where(x => x.Task.IsCompleted));

            foreach (var step in completed.List)
            {
                _ = executing.Remove(step);
                await step.Task; // propagate any errors
            }

            foreach (var res in completed.List.SelectMany(step => step.Step.Outputs))
            {
                stepResults.Add(res);
            }
        }

        await Task.WhenAll(executing.Select(x => x.Task));
    }

    protected virtual async ValueTask<PooledList<Step>> GetAvailableSteps(IEnumerable<Step> steps)
    {
        var list = PooledList<Step>.Get();
        foreach (var step in steps)
        {
            if (await CanRunStep(step))
                list.List.Add(step);
        }

        return list;
    }

    protected virtual async Task Execute(Type type, CancellationToken token)
    {
        await using var scope = Pooled<Scope>.Get();
        IExecutable res;
        using var parms = await ResolveConstructorParameters(type, scope.Value);
        {
            res = Activator.CreateInstance(MapType(type), parms.ToDisposableArray()) as IExecutable
                ?? throw new InvalidOperationException();
        }
        var result = await res.Execute(token);

        using (await containerLock.WaitIf(true))
            IoCContainer.TryAdd(type, res);

        await AddObjectsToContainer(result, false);
    }

    protected virtual async Task Execute(CancellationToken token, params Delegate[] steps)
    {
        token.ThrowIfCancellationRequested();
        foreach (var step in steps)
        {
            await using var scope = Pooled<Scope>.Get();
            await AddObjectsToContainer(await Invoke(step, scope.Value), false);

            token.ThrowIfCancellationRequested();
        }
    }

    public virtual void AddSteps(params Step[] steps)
    {
        foreach (var step in steps)
        {
            if (step.Self.IsType)
            {
                var type = MapType(step.Self.Type!);
                this.steps.Add(FromType(type));
            }
            else
            {
                this.steps.Add(step);
            }
        }
    }

    public async Task<Step> Execute(Step step, CancellationToken token)
    {
        return await Execute(step, true, token);
    }

    protected virtual async Task<Step> Execute(Step step, bool validate, CancellationToken token)
    {
        if (validate)
        {
            await using var scope = Pooled<Scope>.Get();
            if (GetMissingDependencies(step, scope.Value) is { Length: > 0 } missingDeps)
            {
                var deps = String.Join(Environment.NewLine, missingDeps.Select(x => x.Stringify()));
                var message = $"Missing dependencies:{Environment.NewLine}{deps}";
                throw new MissingDependencyException(message);
            }
        }
        if (step.Self.IsType)
            await Execute(step.Self.Type!, token);
        else
            await Execute(token, step.Self.Method!);
        return step;
    }

    private async Task<object?> Invoke(Delegate toInvoke, Scope toDispose)
    {
        using var parms = PooledList<object>.Get();
        foreach (var paramType in toInvoke.Method.GetParameters().Select(p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, toDispose);
            if (value is not null)
            {
                parms.List.Add(value);
            }
            else
            {
                throw new MissingDependencyException("Missing dependency for method "
                    + GetName(toInvoke.Method)
                    + " of type "
                    + toInvoke.Target?.GetType().Name);
            }
        }
        var res = toInvoke.DynamicInvoke(parms.ToDisposableArray());
        if (res is Task task)
        {
            await task;
            return toInvoke.Method.ReturnType.IsGenericType
                && task.GetType().GetProperty(nameof(Task<int>.Result)) is { } ResultProp
                    ? ResultProp.GetValue(task)
                    : null;
        }
        return res;
    }

    public void RegisterSingleton<TDep>(TDep dependency) where TDep : notnull
    {
        if (!IoCContainer.TryAdd(typeof(TDep), dependency))
        {
            throw new Exception($"Duplicate dependency of type {typeof(TDep).FullName}");
        }
        TypeMap.Add(typeof(TDep), typeof(TDep));
    }

    public void RegisterSingletonFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new Exception($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
        }

        SingletonFactories.Add(typeof(TType), factory);
    }

    public void RegisterScopedFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new Exception($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
        }

        ScopedFactories.Add(typeof(TType), factory);
    }

    public void RegisterTransientFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new Exception($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
        }

        TransientFactories.Add(typeof(TType), factory);
    }

    public void RegisterSingletonType<TInterface, TImplementation>() where TImplementation : TInterface
    {
        RegisterTypeMapping<TInterface, TImplementation>();
        SingletonTypes.Add(typeof(TImplementation));
    }

    public void RegisterScopedType<TInterface, TImplementation>() where TImplementation : TInterface
    {
        RegisterTypeMapping<TInterface, TImplementation>();
        ScopedTypes.Add(typeof(TImplementation));
    }

    public void RegisterTransientType<TInterface, TImplementation>() where TImplementation : TInterface
    {
        RegisterTypeMapping<TInterface, TImplementation>();
        TransientTypes.Add(typeof(TImplementation));
    }

    public void RegisterTypeMapping<TInterface, TImplementation>()
    {
        if (TypeMap.ContainsKey(typeof(TInterface)))
            throw new InvalidOperationException("This type is already mapped");

        if (TypeMap.TryGetValue(typeof(TImplementation), out var found) && found != typeof(TImplementation))
            throw new NotSupportedException("Recursive type resolution is not supported");

        TypeMap.Add(typeof(TInterface), typeof(TImplementation));
    }

    private bool IsLocked(Step step)
    {
        return step.Mutexes.Any(mutexes.Contains);
    }

    private async Task AddObjectsToContainer(object? result, bool isLocked)
    {
        if (result is null)
            return;

        using var @lock = await containerLock.WaitIf(!isLocked);

        if (result is IEnumerable<Delegate> delegates)
        {
            foreach (var del in delegates)
            {
                if (!IoCContainer.TryAdd(del.GetType(), del))
                {
                    throw new DuplicateResultException(
                        "Delegate of this type is already registered");
                }
            }
        }
        else if (!IoCContainer.TryAdd(result.GetType(), result))
        {
            throw new DuplicateResultException(
                "Object of this type is already registered");
        }
    }

    private bool CanResolveObject(Type type, Scope scope)
    {
        using var dependencyLoopDetector = scope.CheckDependencyLoop(type);

        if (ResolvableTypes.Contains(type))
        {
            return true;
        }

        if (IoCContainer.ContainsKey(type))
        {
            lock (ResolvableTypes)
                ResolvableTypes.Add(type);
            return true;
        }
        if (SingletonFactories.ContainsKey(type)
            || ScopedFactories.ContainsKey(type)
            || TransientFactories.ContainsKey(type))
        {
            var res =
                (SingletonFactories.GetValueOrDefault(type)
                    ?? ScopedFactories.GetValueOrDefault(type)
                    ?? TransientFactories.GetValueOrDefault(type))
                .Method.GetParameters()
                .Select(x => x.ParameterType)
                .All(t => CanResolveObject(t, scope));

            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(type);
            }

            return res;
        }

        if (SingletonTypes.Contains(type)
            || ScopedTypes.Contains(type)
            || TransientTypes.Contains(type))
        {
            var res = type.GetConstructors()
                .Single(x => !x.IsStatic)
                .GetParameters()
                .Select(x => x.ParameterType)
                .All(t => CanResolveObject(t, scope));
            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(type);
            }

            return res;
        }

        var mappedType = MapType(type);
        return mappedType != type && CanResolveObject(mappedType, scope);
    }

    private async ValueTask<bool> CanRunStep(Step step)
    {
        if (IsLocked(step))
            return false;

        await using var scope = Pooled<Scope>.Get();
        return !GetMissingDependencies(step, scope.Value).Any();
    }

    private StepIO[] GetMissingDependencies(Step step, Scope scope)
    {
        using var result = PooledList<StepIO>.Get();

        foreach (var input in step.Inputs)
        {
            if (input.NamedDependency is not null)
            {
                if (!stepResults.Contains(input))
                    result.List.Add(input);
            }
            else if (input.TypedDependency is { } type)
            {
                if (!CanResolveObject(type, scope) && !stepResults.Contains(input))
                    result.List.Add(input);
            }
            else if (input.ExactValueDependency is { } obj)
            {
                if (!IoCContainer.ContainsKey(obj.GetType())
                    && !stepResults.Contains(input))
                {
                    result.List.Add(input);
                }
            }
        }
        return result.List.Count > 0
            ? result.List.ToArray()
            : Array.Empty<StepIO>();
    }

    private static string GetName(MethodInfo method)
    {
        return method
            .GetCustomAttributes(true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description
            ?? method.Name;
    }

    private static string GetName(Type type)
    {
        return type
            .GetCustomAttributes(true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description
            ?? type.Name;
    }

    protected Type MapType(Type interfaceType)
        => TypeMap.GetValueOrDefault(interfaceType) ?? interfaceType;

    private async ValueTask<PooledList<object>> ResolveConstructorParameters(Type type, Scope scope)
    {
        var parms = PooledList<object>.Get();
        try
        {
            var ctor = MapType(type).GetConstructors().Single();
            foreach (var paramType in ctor.GetParameters().Select(p => p.ParameterType))
            {
                var value = await ResolveObject(paramType, scope);
                if (value is not null)
                {
                    parms.List.Add(value);
                }
                else
                {
                    throw new Exception("Missing dependency of type " + paramType.Name + " for Step "
                        + GetName(type)
                        + ". Make sure you execute everything in proper order");
                }
            }
        }
        catch
        {
            parms.Dispose();
            throw;
        }

        return parms;
    }

    private TValue? FindValue<TValue>(IDictionary<Type, TValue> dict, Type key) where TValue : class
    {
        return dict.TryGetValue(key, out var value) || dict.TryGetValue(MapType(key), out value)
            ? value
            : null;
    }

    protected virtual async Task<object?> ResolveObject(Type paramType, Scope scope)
    {
        using var dependencyLoopDetector = scope.CheckDependencyLoop(paramType);

        var value = FindValue(IoCContainer, paramType) ?? FindValue(scope.Scoped, paramType);
        if (value is not null)
            return value;

        if (FindValue(SingletonFactories, paramType) is { } factory)
        {
            var obj = await Invoke(factory, scope);
            if (obj is not null)
            {
                lock (IoCContainer)
                    IoCContainer.TryAdd(paramType, obj);
                if (obj.GetType() != paramType)
                {
                    lock (TypeMap)
                        TypeMap.TryAdd(paramType, obj.GetType());
                }
            }

            return obj;
        }
        if (SingletonTypes.Contains(paramType) || SingletonTypes.Contains(MapType(paramType)))
        {
            using var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToDisposableArray())!;

            lock (IoCContainer)
                IoCContainer.TryAdd(newType, res);

            return res;
        }

        if (FindValue(ScopedFactories, paramType) is { } scopedFactory)
        {
            var obj = await Invoke(scopedFactory, scope);
            scope.Register(obj);
            return obj;
        }
        if (ScopedTypes.Contains(paramType) || ScopedTypes.Contains(MapType(paramType)))
        {
            using var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToDisposableArray())!;

            scope.Register(res);

            return res;
        }

        if (FindValue(TransientFactories, paramType) is { } transientFactory)
        {
            var result = await Invoke(transientFactory, scope);
            scope.AddDisposable(result);
            return result;
        }
        if (TransientTypes.Contains(paramType) || TransientTypes.Contains(MapType(paramType)))
        {
            using var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToDisposableArray())!;

            scope.AddDisposable(res);

            return res;
        }

        return null;
    }

    protected readonly Dictionary<Type, object> IoCContainer = new();
    protected readonly Dictionary<Type, Type> TypeMap = new();
    protected readonly HashSet<Type> ResolvableTypes = new();
    protected readonly HashSet<Type> TransientTypes = new();
    protected readonly HashSet<Type> ScopedTypes = new();
    protected readonly HashSet<Type> SingletonTypes = new();
    protected readonly Dictionary<Type, Delegate> SingletonFactories = new();
    protected readonly Dictionary<Type, Delegate> ScopedFactories = new();
    protected readonly Dictionary<Type, Delegate> TransientFactories = new();

    private readonly List<Step> steps = new();
    private readonly HashSet<StepIO> stepResults = new(EqualityComparer<StepIO>.Default);
    private readonly HashSet<StepIO> mutexes = new();
    private readonly HashSet<(Step Step, Task Task)> executing = new();
    private readonly SemaphoreSlim containerLock = new(1, 1);

    protected class Scope : IAsyncDisposable
    {
        public void Register(object? possibleDisposable)
        {
            if (possibleDisposable == null)
                return;
            Scoped[possibleDisposable.GetType()] = possibleDisposable;
            AddDisposable(possibleDisposable);
        }

        public void AddDisposable(object? possibleDisposable)
        {
            if (possibleDisposable == null)
                return;
            if (possibleDisposable is IDisposable disp)
                Disposables.Push(disp);
            else if (possibleDisposable is IAsyncDisposable asyncDisp)
                Disposables.Push(asyncDisp);
        }

        public async ValueTask DisposeAsync()
        {
            Scoped.Clear();
            ParameterTypesPreviouslyAttemptedToCreate.Clear();
            while (Disposables.TryPop(out var popped))
            {
                if (popped is IAsyncDisposable aDisp)
                    await aDisp.DisposeAsync();
                else if (popped is IDisposable disp)
                    disp.Dispose();
            }
        }
        public Stack<object> Disposables { get; } = new();
        public Dictionary<Type, object> Scoped { get; } = new();

        protected internal DependencyLoopDetector CheckDependencyLoop(Type parameterType)
        {
            return new DependencyLoopDetector(this, parameterType);
        }

        private HashSet<Type> ParameterTypesPreviouslyAttemptedToCreate { get; } = new();

        protected internal readonly struct DependencyLoopDetector : IDisposable
        {
            private readonly Scope scope;
            private readonly Type type;

            public DependencyLoopDetector(Scope scope, Type type)
            {
                this.scope = scope;
                this.type = type;
                if (!scope.ParameterTypesPreviouslyAttemptedToCreate.Add(type))
                    throw new DependencyLoopException($"Found dependency loop while trying to resolve object of type {type.Name}");
            }
            public void Dispose() => scope.ParameterTypesPreviouslyAttemptedToCreate.Remove(type);
        }
    }

    protected PooledList<T> ToListFromPool<T>(IEnumerable<T> values)
    {
        var list = PooledList<T>.Get();
        list.List.AddRange(values);
        return list;
    }
}
