using System.ComponentModel;
using System.Diagnostics;
using System.Linq.Expressions;

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
                    executing.Add((step, RunStep(step, token)));
                }
            }

            if (executing.Count == 0)
                throw new LeftOverStepException("Could not execute all steps");

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
                results.Add(res);
            }
        }

        await Task.WhenAll(executing.Select(x => x.Task));
    }

    public virtual async Task Execute(Type type, CancellationToken token)
    {
        await using var stack = new Scope();
        var parms = await ResolveConstructorParameters(type, stack);
        var res = Activator.CreateInstance(ResolveType(type), parms.ToArray()) as IExecutable;
        var result = await res!.Execute(token);
        await RegisterStepOutput(type, res, false);
        await AddObjectsToContainer(result, false);
    }

    public virtual async Task Execute(CancellationToken token, params Delegate[] steps)
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
                var type = ResolveType(step.Self.Type!);
                this.steps.Add(FromType(type));
            }
            else
            {
                this.steps.Add(step);
            }
        }
    }

    public void RegisterSingleton<TDep>(TDep dependency) where TDep : notnull
    {
        if (!IoCContainer.TryAdd(typeof(TDep), dependency))
        {
            throw new Exception($"Duplicate dependency of type {typeof(TDep).FullName}");
        }
        SingletonTypeMap.Add(typeof(TDep), typeof(TDep));
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

    public void RegisterType<TInterface, TImplementation>() where TImplementation : TInterface
    {
        SingletonTypeMap.Add(typeof(TInterface), typeof(TImplementation));
    }

    public void RegisterType<TService>()
    {
        SingletonTypeMap.Add(typeof(TService), typeof(TService));
    }

    private bool IsLocked(Step step)
    {
        return step.Mutexes.Any(mutexes.Contains);
    }

    private async Task AddObjectsToContainer(object result, bool isLocked)
    {
        try
        {
            if (!isLocked)
            {
                await containerLock.WaitAsync();
            }

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
        finally
        {
            if (!isLocked)
            {
                _ = containerLock.Release();
            }
        }
    }

    private bool CanRunStep(Step step)
    {
        if (IsLocked(step))
            return false;

        foreach (var input in step.Inputs)
        {
            if (input.NamedDependency is not null)
            {
                if (!results.Contains(input))
                    return false;
            }
            else if (input.TypedDependency is { } type)
            {
                if (!IoCContainer.ContainsKey(type)
                    && !SingletonFactories.ContainsKey(type)
                    && !TransientFactories.ContainsKey(type)
                    && !SingletonTypeMap.ContainsKey(type)
                    && !results.Contains(input))
                {
                    return false;
                }
            }
            else if (input.ExactValueDependency is { } obj)
            {
                if (!IoCContainer.ContainsKey(obj.GetType())
                    && !results.Contains(input))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<Step> RunStep(Step step, CancellationToken token)
    {
        if (step.Self.IsType)
            await Execute(step.Self.Type!, token);
        else
            await Execute(token, step.Self.Method!);
        return step;
    }

    //private bool CanOverwrite(Step step, object result)
    //{
    //    if (step.Inputs)
    //}

    private async Task<object?> Invoke(Delegate toInvoke, Scope toDispose, bool addToContainer = true, bool wasLocked = false)
    {
        var parms = new List<object>();
        foreach (var paramType in toInvoke.Method.GetParameters().Select(p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, toDispose, wasLocked);
            if (value is not null)
            {
                parms.Add(value);
            }
            else
            {
                throw new Exception("Missing dependency for method "
                    + GetName(toInvoke.Method, true)
                    + " of type "
                    + toInvoke.Target?.GetType().Name);
            }
        }
        var res = toInvoke.DynamicInvoke(parms.ToArray());
        if (res is Task task)
        {
            await task;
            if (toInvoke.Method.ReturnType.IsGenericType && task.GetType().GetProperty(nameof(Task<int>.Result)) is { } ResultProp)
            {
                var result = ResultProp.GetValue(task);
                if (result is not null && addToContainer)
                    await AddObjectsToContainer(result, wasLocked);

                return result;
            }
            return null;
        }
        else if (res is not null)
        {
            if (addToContainer)
                await AddObjectsToContainer(res, wasLocked);

            return res;
        }
        return null;
    }

    private static string GetName(MethodInfo method, bool ignoreGeneratedName = false)
    {
        var res = method
            .GetCustomAttributes(true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description
            ?? method.Name;

        Debug.Assert(ignoreGeneratedName || method.Name != res || !res.Contains('<'), "Please use Description attribute or method with actual name");
        return res;
    }

    private static string GetName(Type type)
    {
        var res = type
            .GetCustomAttributes(true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault()
            ?.Description
            ?? type.Name;

        Debug.Assert(type.Name != res || !res.Contains('<'), "Please use Description attribute or method with actual name");
        return res;
    }

    protected Type ResolveType(Type interfaceType)
        => SingletonTypeMap.GetValueOrDefault(interfaceType) ?? interfaceType;

    private async Task<List<object>> ResolveConstructorParameters(Type type, Scope scope, bool isLocked = false)
    {
        var parms = new List<object>();
        var ctor = ResolveType(type).GetConstructors().Single();
        foreach (var paramType in ctor.GetParameters().Select(p => p.ParameterType))
        {
            var value = await ResolveObject(paramType, scope, isLocked);
            if (value is not null)
            {
                parms.Add(value);
            }
            else
            {
                throw new Exception("Missing dependency of type " + paramType.Name + " for Step "
                    + type.Name
                    + ". Make sure you execute everything in proper order");
            }
        }

        return parms;
    }

    protected virtual async Task<object?> ResolveObject(Type paramType, Scope scope, bool wasLocked)
    {
        try
        {
            if (!wasLocked)
            {
                await containerLock.WaitAsync();
            }

            if (IoCContainer.TryGetValue(paramType, out var value)
                || IoCContainer.TryGetValue(ResolveType(paramType), out value))
            {
                return value;
            }

            if (scope.Scoped.TryGetValue(paramType, out value)
                || scope.Scoped.TryGetValue(ResolveType(paramType), out value))
            {
                return value;
            }

            if (SingletonFactories.TryGetValue(paramType, out var fact)
                || SingletonFactories.TryGetValue(ResolveType(paramType), out fact))
            {
                var obj = await Invoke(fact, scope, true, true);
                if (obj is not null && obj.GetType() != paramType)
                {
                    lock (SingletonTypeMap)
                        SingletonTypeMap.Add(paramType, obj.GetType());
                }

                return obj;
            }

            if (ScopedFactories.TryGetValue(paramType, out fact)
                || ScopedFactories.TryGetValue(ResolveType(paramType), out fact))
            {
                var obj = await Invoke(fact, scope, true, true);
                if (obj is not null && obj.GetType() != paramType)
                {
                    lock (ScopedTypeMap)
                        ScopedTypeMap.Add(paramType, obj.GetType());
                    scope.Register(obj);
                }

                return obj;
            }

            if (TransientFactories.TryGetValue(paramType, out fact)
                || TransientFactories.TryGetValue(ResolveType(paramType), out fact))
            {
                var result = await Invoke(fact, scope, false, true);
                if (result is not null)
                    scope.Register(result);
                return result;
            }

            if (SingletonTypeMap.TryGetValue(paramType, out var toCreate))
            {
                var parms = await ResolveConstructorParameters(toCreate, scope, true);
                return await CreateAndRegisterInstance(toCreate, parms, true);
            }

            return null;
        }
        finally
        {
            if (!wasLocked)
                containerLock.Release();
        }
    }

    private async Task<object> CreateAndRegisterInstance(Type type, List<object> parms, bool ignoreLock)
    {
        type = ResolveType(type);
        var res = Activator.CreateInstance(type, parms.ToArray())!;
        await RegisterStepOutput(type, res, ignoreLock);
        return res;
    }

    private async Task RegisterStepOutput(Type type, object res, bool ignoreLock)
    {
        try
        {
            if (!ignoreLock)
            {
                await containerLock.WaitAsync();
            }

            if (!IoCContainer.ContainsKey(type))
            {
                IoCContainer.Add(type, res);
            }
        }
        finally
        {
            if (!ignoreLock)
            {
                _ = containerLock.Release();
            }
        }
    }

    protected readonly Dictionary<Type, object> IoCContainer = new();
    protected readonly Dictionary<Type, Type> SingletonTypeMap = new();
    protected readonly Dictionary<Type, Type> ScopedTypeMap = new();
    protected readonly Dictionary<Type, Type> TransientTypeMap = new();
    protected readonly Dictionary<Type, Delegate> SingletonFactories = new();
    protected readonly Dictionary<Type, Delegate> ScopedFactories = new();
    protected readonly Dictionary<Type, Delegate> TransientFactories = new();

    private readonly List<Step> steps = new();
    private readonly HashSet<StepIO> results = new(EqualityComparer<StepIO>.Default);
    private readonly HashSet<StepIO> mutexes = new();
    private readonly HashSet<(Step Step, Task Task)> executing = new();
    private readonly SemaphoreSlim containerLock = new(1, 1);

    protected class Scope
    {
        public void Register(object possibleDisposable)
        {
            if (possibleDisposable == null)
                return;
            Scoped[possibleDisposable.GetType()] = possibleDisposable;
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
