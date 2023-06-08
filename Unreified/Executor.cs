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
            var available = steps.Where(CanRunStep).ToList();

            var toRun = new List<Step>();
            foreach (var step in available)
            {
                if (executing.Count == maxDegreeOfParallelism)
                    break;

                if (!IsLocked(step))
                {
                    toRun.Add(step);
                    executing.Add((step, Execute(step, token)));
                }
            }

            if (executing.Count == 0)
                throw new LeftOverStepException("Could not execute all steps. Make sure all dependencies can be resolved");

            foreach (var step in toRun)
                _ = steps.Remove(step);

            _ = await Task.WhenAny(executing.Select(x => x.Task));
            var completed = executing.Where(x => x.Task.IsCompleted).ToList();

            foreach (var step in completed)
            {
                _ = executing.Remove(step);
                await step.Task; // propagate any errors
            }

            foreach (var res in completed.SelectMany(step => step.Step.Outputs))
            {
                stepResults.Add(res);
            }
        }

        await Task.WhenAll(executing.Select(x => x.Task));
    }

    protected virtual async Task Execute(Type type, CancellationToken token)
    {
        await using var stack = new Scope();
        var parms = await ResolveConstructorParameters(type, stack);
        var res = Activator.CreateInstance(MapType(type), parms.ToArray()) as IExecutable;
        var result = await res!.Execute(token);

        using (await containerLock.WaitIf(true))
            IoCContainer.TryAdd(type, res);

        await AddObjectsToContainer(result, false);
    }

    protected virtual async Task Execute(CancellationToken token, params Delegate[] steps)
    {
        token.ThrowIfCancellationRequested();
        foreach (var step in steps)
        {
            await using var stack = new Scope();
            await Invoke(step, stack);

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
        if (step.Self.IsType)
            await Execute(step.Self.Type!, token);
        else
            await Execute(token, step.Self.Method!);
        return step;
    }

    private async Task<object?> Invoke(Delegate toInvoke, Scope toDispose)
    {
        var parms = new List<object>();
        foreach (var paramType in toInvoke.Method.GetParameters().Select(p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, toDispose);
            if (value is not null)
            {
                parms.Add(value);
            }
            else
            {
                throw new MissingDependencyException("Missing dependency for method "
                    + GetName(toInvoke.Method)
                    + " of type "
                    + toInvoke.Target?.GetType().Name);
            }
        }
        var res = toInvoke.DynamicInvoke(parms.ToArray());
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

    private async Task AddObjectsToContainer(object result, bool isLocked)
    {
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
        else
        {
            if (!IoCContainer.TryAdd(result.GetType(), result))
            {
                throw new DuplicateResultException(
                    "Object of this type is already registered");
            }
        }
    }

    private bool CanResolveObject(Type type)
    {
        var mappedType = MapType(type);
        if (ResolvableTypes.Contains(type) || ResolvableTypes.Contains(mappedType))
        {
            return true;
        }

        if (IoCContainer.ContainsKey(type)
            || SingletonFactories.ContainsKey(type)
            || ScopedFactories.ContainsKey(type)
            || TransientFactories.ContainsKey(type)
            || SingletonTypes.Contains(type)
            || ScopedTypes.Contains(type)
            || TransientTypes.Contains(type))
        {
            var res = type.GetConstructors()
                .Single(x => !x.IsStatic)
                .GetParameters()
                .Select(x => x.ParameterType)
                .All(CanResolveObject);
            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(type);
            }

            return res;
        }

        if (IoCContainer.ContainsKey(mappedType)
            || SingletonFactories.ContainsKey(mappedType)
            || ScopedFactories.ContainsKey(mappedType)
            || TransientFactories.ContainsKey(mappedType)
            || SingletonTypes.Contains(mappedType)
            || ScopedTypes.Contains(mappedType)
            || TransientTypes.Contains(mappedType))
        {
            var res = mappedType.GetConstructors()
                .Single(x => !x.IsStatic)
                .GetParameters()
                .Select(x => x.ParameterType)
                .All(CanResolveObject);
            if (res)
            {
                lock (ResolvableTypes)
                    ResolvableTypes.Add(mappedType);
            }

            return res;
        }

        return false;
    }

    private bool CanRunStep(Step step)
    {
        if (IsLocked(step))
            return false;

        foreach (var input in step.Inputs)
        {
            if (input.NamedDependency is not null)
            {
                if (!stepResults.Contains(input))
                    return false;
            }
            else if (input.TypedDependency is { } type)
            {
                if (!CanResolveObject(type) && !stepResults.Contains(input))
                {
                    return false;
                }
            }
            else if (input.ExactValueDependency is { } obj)
            {
                if (!IoCContainer.ContainsKey(obj.GetType())
                    && !stepResults.Contains(input))
                {
                    return false;
                }
            }
        }

        return true;
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

    private async Task<List<object>> ResolveConstructorParameters(Type type, Scope scope)
    {
        var parms = new List<object>();
        var ctor = MapType(type).GetConstructors().Single();
        foreach (var paramType in ctor.GetParameters().Select(p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, scope);
            if (value is not null)
            {
                parms.Add(value);
            }
            else
            {
                throw new Exception("Missing dependency of type " + paramType.Name + " for Step "
                    + GetName(type)
                    + ". Make sure you execute everything in proper order");
            }
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
            var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToArray())!;

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
            var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToArray())!;

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
            var parms = await ResolveConstructorParameters(paramType, scope);

            var newType = MapType(paramType);
            var res = Activator.CreateInstance(newType, parms.ToArray())!;

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

    protected class Scope
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

        public async Task DisposeAsync()
        {
            Scoped.Clear();
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
    }
}
