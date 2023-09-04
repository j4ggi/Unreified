using System.Collections.Concurrent;

using static Unreified.Step;

namespace Unreified;
public class Executor : ICloneable, IAsyncDisposable
{
    public async Task RunAll(CancellationToken cancellationToken) => await RunAll(1, cancellationToken);

    public async Task RunAll(byte maxDegreeOfParallelism, CancellationToken token)
    {
        using var exeLocker = await executionLocker.LockAsync();

        while (steps.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            using (await stepsLocker.LockAsync())
            using (var toRun = PooledList<Step>.Get())
            {
                await foreach (var step in GetAvailableSteps(steps))
                {
                    if (executing.Count == maxDegreeOfParallelism)
                        break;

                    token.ThrowIfCancellationRequested();
                    if (!IsLocked(step))
                    {
                        toRun.List.Add(step);
                        foreach (var mutex in step.Mutexes)
                            mutexes.Add(mutex);
                        executing.Add((step, Execute(step, false, token)));
                    }
                }

                if (executing.Count == 0)
                    await ReportLeftoverSteps();

                foreach (var step in toRun.List)
                {
                    _ = steps.Remove(step);
                    foreach (var mutex in step.Mutexes)
                        mutexes.Remove(mutex);
                }
            }

            _ = await Task.WhenAny(executing.Select(static x => x.Task));
            using var completed = ToPooledList(executing.Where(static x => x.Task.IsCompleted));

            foreach (var step in completed.List)
            {
                _ = executing.Remove(step);
                await step.Task; // propagate any errors
            }

            foreach (var res in completed.List.SelectMany(static step => step.Step.Outputs))
                stepResults.Add(res);
        }

        await Task.WhenAll(executing.Select(static x => x.Task));

        async Task ReportLeftoverSteps()
        {
            var missingDeps = new List<StepIO>();
            foreach (var step in steps)
            {
                using var missing = await GetMissingDependencies(step);
                if (missing.List.Any())
                    missingDeps.AddRange(missing.List);
            }
            var leftoverSteps = string.Join(Environment.NewLine, steps.Select(static x => x.Self));
            var deps = String.Join(Environment.NewLine, missingDeps.Select(static x => x.Stringify()));
            var missingDepsMessage = $"Missing dependencies:{Environment.NewLine}{deps}";
            throw new LeftOverStepException(
                $"Could not execute following steps:{Environment.NewLine}{leftoverSteps}." +
                $"{Environment.NewLine}{missingDepsMessage}");
        }
    }

    protected virtual async IAsyncEnumerable<Step> GetAvailableSteps(IEnumerable<Step> steps)
    {
        using var tempMutexes = PooledSet<StepIO>.Get();
        foreach (var step in steps)
        {
            if (!await CanRunStep(step))
                continue;

            bool skipOuter = false;
            foreach (var m in step.Mutexes)
            {
                if (!tempMutexes.Set.Add(m))
                {
                    skipOuter = true;
                    break;
                }
            }

            if (skipOuter)
                continue;

            yield return step;
        }
    }

    protected virtual async Task Execute(Type type, CancellationToken token)
    {
        await using var scope = Pooled<ExecutionScope>.Get();
        IExecutable res;
        using (var parms = await ResolveConstructorParameters(type, scope.Value))
        {
            res = Activator.CreateInstance(MapType(type), parms.ToDisposableArray()) as IExecutable
                ?? throw new InvalidOperationException($"Type {type.Name} is not {nameof(IExecutable)}");
        }
        var result = await res.Execute(token);

        using (await containerLocker.WaitIf(true))
            IoCContainer.TryAdd(type, res);

        await AddObjectsToContainer(result, false);
    }

    /// <summary>
    /// Creates and instance of given type using available dependencies
    /// </summary>
    /// <typeparam name="T">Type of item to create</typeparam>
    /// <returns>Requested service with a scope to dispose which contains disposable dependencies created for this service and the service itself (if it is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>)</returns>
    public virtual async Task<ServiceWithScope<T>> Instantiate<T>()
    {
        var scope = Pooled<ExecutionScope>.Get();
        using var parms = await ResolveConstructorParameters(typeof(T), scope.Value);
        var service = (T)Activator.CreateInstance(MapType(typeof(T)), parms.ToDisposableArray())!;
        scope.Value.AddDisposable(service);
        return new(scope, service);
    }

    protected virtual async Task Execute(CancellationToken token, Delegate step)
    {
        token.ThrowIfCancellationRequested();
        await using var scope = Pooled<ExecutionScope>.Get();
        await AddObjectsToContainer(await Invoke(step, scope.Value), false);
        token.ThrowIfCancellationRequested();
    }

    public virtual void AddSteps(params Step[] steps)
    {
        using var _ = stepsLocker.Lock();
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

    public virtual async ValueTask AddStepsAsync(params Step[] steps)
    {
        using var _ = await stepsLocker.LockAsync();
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
        using var exeLocker = await executionLocker.LockAsync();
        return await Execute(step, true, token);
    }

    protected virtual async Task<Step> Execute(Step step, bool validate, CancellationToken token)
    {
        if (validate)
        {
            using var missingDeps = await GetMissingDependencies(step);
            if (missingDeps.List.Any())
            {
                var deps = String.Join(Environment.NewLine, missingDeps.List.Select(static x => x.Stringify()));
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

    private async Task<object?> Invoke(Delegate toInvoke, ExecutionScope toDispose)
    {
        using var parms = PooledList<object>.Get();
        foreach (var paramType in toInvoke.Method.GetParameters().Select(static p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, toDispose);
            if (value is null)
            {
                throw new MissingDependencyException("Missing dependency for method "
                    + GetName(toInvoke.Method)
                    + " of type "
                    + toInvoke.Target?.GetType().Name);
            }

            parms.List.Add(value);
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
        // this should be safe because we never actually awaited ValueTask until this point
        else if (res is ValueTask valueTask)
        {
            var asTask = valueTask.AsTask();
            await asTask;
            return null;
        }
        else if (IsGenericValueTask(toInvoke.Method.ReturnType))
        {
            var method =
                GenericValueTaskToTaskConverter.GetOrAdd(
                    toInvoke.Method.ReturnType,
                    type => typeof(Executor)
                        .GetMethod(nameof(ToTask))!
                        .MakeGenericMethod(type.GetGenericArguments()));
            var asTask = (Task)method.Invoke(null, new[] { res })!;
            await asTask;
            return asTask.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(asTask);
        }
        return res;
    }

    private static readonly ConcurrentDictionary<Type, MethodInfo> GenericValueTaskToTaskConverter = new();

    private static bool IsGenericValueTask(Type returnType)
    {
        return returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>);
    }

    private static Task<T> ToTask<T>(ValueTask<T> vt) => vt.AsTask();

    public void RegisterSingleton<TDep>(TDep dependency) where TDep : notnull
    {
        if (!IoCContainer.TryAdd(typeof(TDep), dependency))
        {
            throw new InvalidOperationException($"Duplicate dependency of type {typeof(TDep).FullName}");
        }
        TypeMap.Add(typeof(TDep), typeof(TDep));
    }

    public void RegisterSingletonFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new InvalidOperationException($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
        }

        SingletonFactories.Add(typeof(TType), factory);
    }

    public void RegisterScopedFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new InvalidOperationException($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
        }

        ScopedFactories.Add(typeof(TType), factory);
    }

    public void RegisterTransientFactory<TType>(Delegate factory)
    {
        if (!factory.Method.ReturnType.IsAssignableTo(typeof(TType))
            && !factory.Method.ReturnType.IsAssignableTo(typeof(Task<TType>)))
        {
            throw new InvalidOperationException($"{factory.Method.ReturnType.Name} is not assignable to {typeof(TType).Name}");
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

        using var @lock = await containerLocker.WaitIf(!isLocked);

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

    private readonly Dictionary<Delegate, List<Type>> DelegateParameterCache = new();
    private readonly Dictionary<Type, List<Type>> TypeParameterCache = new();

    private bool CanResolveObject(Type type, ExecutionScope scope)
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

        if (SingletonFactories.TryGetValue(type, out var factory)
            || ScopedFactories.TryGetValue(type, out factory)
            || TransientFactories.TryGetValue(type, out factory))
        {
            List<Type> types;
            lock (DelegateParameterCache)
            {
                types = DelegateParameterCache.TryGetValue(factory, out var found)
                    ? found
                    : DelegateParameterCache[factory] = factory.Method.GetParameters()
                        .SelectList(static x => x.ParameterType);
            }
            var res = CanResolveAllObjects(types, scope);
            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(type);
                lock (DelegateParameterCache)
                    DelegateParameterCache.Remove(factory);
            }
            return res;
        }

        var isRegisteredForCreation = SingletonTypes.Contains(type)
            || ScopedTypes.Contains(type)
            || TransientTypes.Contains(type);

        if (!type.IsInterface && !type.IsAbstract && isRegisteredForCreation)
        {
            List<Type> types;
            lock (TypeParameterCache)
            {
                types = TypeParameterCache.TryGetValue(type, out var found)
                    ? found
                    : TypeParameterCache[type] = type.GetConstructors()
                        .Single(static x => !x.IsStatic)
                        .GetParameters()
                        .SelectList(static x => x.ParameterType);
            }
            var res = CanResolveAllObjects(types, scope);
            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(type);
                lock (TypeParameterCache)
                    TypeParameterCache.Remove(type);
            }

            return res;
        }

        var mappedType = MapType(type);
        return mappedType != type && CanResolveObject(mappedType, scope);
    }

    private bool CanResolveAllObjects(IEnumerable<Type> types, ExecutionScope scope)
    {
        foreach (var type in types)
        {
            if (!CanResolveObject(type, scope))
                return false;
        }
        return true;
    }

    private async ValueTask<bool> CanRunStep(Step step)
    {
        if (IsLocked(step))
            return false;

        using var missing = await GetMissingDependencies(step);
        return !missing.List.Any();
    }

    private async ValueTask<PooledList<StepIO>> GetMissingDependencies(Step step)
    {
        await using var scope = Pooled<ExecutionScope>.Get();
        var result = PooledList<StepIO>.Get();

        foreach (var input in step.Inputs)
        {
            if (input.NamedDependency is not null)
            {
                if (!stepResults.Contains(input))
                    result.List.Add(input);
            }
            else if (input.TypedDependency is { } type)
            {
                if (!CanResolveObject(type, scope.Value) && !stepResults.Contains(input))
                    result.List.Add(input);
            }
            else if (input.ExactValueDependency is { } obj)
            {
                var hasResult = stepResults.Contains(input);
                var hasSingleton = IoCContainer.TryGetValue(obj.GetType(), out var found) && found.Equals(obj);
                if (!hasResult && !hasSingleton)
                    result.List.Add(input);
            }
        }

        return result;
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

    private async ValueTask<PooledList<object>> ResolveConstructorParameters(Type type, ExecutionScope scope)
    {
        var parms = PooledList<object>.Get();
        try
        {
            var ctor = MapType(type).GetConstructors().Single();
            foreach (var paramType in ctor.GetParameters().Select(static p => p.ParameterType))
            {
                var value = await ResolveObject(paramType, scope);
                if (value is null)
                {
                    throw new MissingDependencyException("Missing dependency of type " + paramType.Name + " for Step "
                        + GetName(type)
                        + ". Make sure you execute everything in proper order");
                }
                parms.List.Add(value);
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

    protected virtual async Task<object?> ResolveObject(Type paramType, ExecutionScope scope)
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

    /// <summary>
    /// Get a new instance of <see cref="Executor"/> with all registered results, dependencies and factories.
    /// Cannot be used during execution. Does not copy registered steps.
    /// Any changes to the new instance will not affect the original instance.
    /// Any later changes to original instance will not affect the new instance.
    /// </summary>
    public async Task<Executor> Clone()
    {
        var res = new Executor();

        using var lock3 = await executionLocker.LockAsync();
        using var lock1 = await containerLocker.LockAsync();
        using var lock2 = await stepsLocker.LockAsync();

        CopyDict(IoCContainer, res.IoCContainer);
        CopyDict(TypeMap, res.TypeMap);
        CopyDict(SingletonFactories, res.SingletonFactories);
        CopyDict(ScopedFactories, res.ScopedFactories);
        CopyDict(TransientFactories, res.TransientFactories);
        CopySet(ResolvableTypes, res.ResolvableTypes);
        CopySet(TransientTypes, res.TransientTypes);
        CopySet(ScopedTypes, res.ScopedTypes);
        CopySet(SingletonTypes, res.SingletonTypes);
        CopySet(stepResults, res.stepResults);

        return res;
        static void CopyDict<TKey, TValue>(IDictionary<TKey, TValue> source, IDictionary<TKey, TValue> target)
        {
            foreach (var item in source)
                target.Add(item.Key, item.Value);
        }
        static void CopySet<TValue>(HashSet<TValue> source, HashSet<TValue> target)
        {
            foreach (var item in source)
                target.Add(item);
        }
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
    private readonly SemaphoreSlim containerLocker = new(1, 1);
    private readonly SemaphoreSlim stepsLocker = new(1, 1);
    private readonly SemaphoreSlim executionLocker = new(1, 1);

    protected class ExecutionScope : IAsyncDisposable
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
            if (possibleDisposable is IDisposable or IAsyncDisposable)
                Disposables.Push(possibleDisposable);
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
            private readonly ExecutionScope scope;
            private readonly Type type;

            public DependencyLoopDetector(ExecutionScope scope, Type type)
            {
                this.scope = scope;
                this.type = type;
                if (!scope.ParameterTypesPreviouslyAttemptedToCreate.Add(type))
                    throw new DependencyLoopException($"Found dependency loop while trying to resolve object of type {type.Name}");
            }
            public void Dispose() => scope.ParameterTypesPreviouslyAttemptedToCreate.Remove(type);
        }
    }

    protected PooledList<T> ToPooledList<T>(IEnumerable<T> values)
    {
        var list = PooledList<T>.Get();
        list.List.AddRange(values);
        return list;
    }

    object ICloneable.Clone() => this.Clone();

    public async ValueTask DisposeAsync()
    {
        foreach (var item in IoCContainer.Values)
        {
            if (item is IDisposable d)
                d.Dispose();
            else if (item is IAsyncDisposable ad)
                await ad.DisposeAsync();
        }
    }
}
