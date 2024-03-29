﻿using System.Linq.Expressions;
using System.Reflection.Emit;

namespace Unreified;
public record class Step
{
    protected Step(
        StepSignature self,
        IList<StepIO>? outputs,
        IList<StepIO>? inputs,
        IList<StepIO>? mutexes)
    {
        Self = self;
        Mutexes = mutexes is null || mutexes.Count == 0
            ? Array.Empty<StepIO>()
            : new ReadOnlyCollection<StepIO>(mutexes);
        Outputs = outputs is null || outputs.Count == 0
            ? Array.Empty<StepIO>()
            : new ReadOnlyCollection<StepIO>(outputs);
        Inputs = inputs is null || inputs.Count == 0
            ? Array.Empty<StepIO>()
            : new ReadOnlyCollection<StepIO>(inputs);
    }
    public StepSignature Self { get; }
    public IList<StepIO> Mutexes { get; }
    public IReadOnlyCollection<StepIO> Outputs { get; }
    public IReadOnlyCollection<StepIO> Inputs { get; }

    public readonly record struct StepIO
    {
        private StepIO(object? exactValueDependency, string? namedDependency, Type? typedDependency)
        {
            NamedDependency = namedDependency;
            TypedDependency = typedDependency;
            ExactValueDependency = exactValueDependency;
        }

        public StepIO(string namedDependency) : this(null, namedDependency ?? throw new ArgumentNullException(nameof(namedDependency)), null) { }
        public StepIO(Type typedDependency) : this(null, null, typedDependency ?? throw new ArgumentNullException(nameof(typedDependency))) { }
        public StepIO(object exactValueDependency) : this(exactValueDependency ?? throw new ArgumentNullException(nameof(exactValueDependency)), null, null) { }

        public string? NamedDependency { get; }
        public Type? TypedDependency { get; }
        public object? ExactValueDependency { get; }

        internal string Stringify()
            => ExactValueDependency is { } value
                ? $"Value: {value}"
                : NamedDependency is { } name
                    ? $"Name: {name}"
                    : $"Type: {TypedDependency!.Name}";
    }

    public readonly record struct StepSignature
    {
        public StepSignature(Delegate del) : this(del, null) { }
        public StepSignature(Type type) : this(null, type) { }

        private StepSignature(Delegate? m, Type? t)
        {
            Method = m;
            Type = t;
            Name = m is not null ? GetName(m.Method) : GetName(t!);
        }

        public Delegate? Method { get; }
        public Type? Type { get; }
        public string Name { get; }
        public bool IsMethod => Method != null;
        public bool IsType => Type != null;

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

        public override string ToString() =>
            IsType
                ? $"Type {Type!.Name}"
                : $"Method {Method!.Method.Name}";
    }

    public static Step FromMethod(Delegate method)
    {
        var parms = method.Method.GetParameters().Select(x => new StepIO(x.ParameterType));
        var inputs = GetIO<InputAttribute>(method.Method.GetCustomAttributes());
        var outputs = GetIO<OutputAttribute>(method.Method.GetCustomAttributes());
        var mutexes = GetIO<MutuallyExclusiveAttribute>(method.Method.GetCustomAttributes());

        return new Step(
            self: new(method),
            outputs: outputs.Distinct().ToList(),
            inputs: parms.Concat(inputs).Distinct().ToList(),
            mutexes: mutexes.Distinct().ToList());
    }

    public static Step FromType<TType>() where TType : IExecutable
        => FromType(typeof(TType));

    public static Step FromType(Type type)
    {
        if (!type.IsAssignableTo(typeof(IExecutable)))
            throw new InvalidOperationException($"{type.Name} must be assignable to {typeof(IExecutable).FullName}");

        var isAbstract = type.IsAbstract || type.IsInterface;

        var parms = type.GetConstructors().Single(x => !x.IsStatic).GetParameters();
        var inputs = GetIO<InputAttribute>(type.GetCustomAttributes());
        var outputs = GetIO<OutputAttribute>(type.GetCustomAttributes());
        var mutexes = GetIO<MutuallyExclusiveAttribute>(type.GetCustomAttributes());
        outputs.Add(new StepIO(type));

        if (!isAbstract)
        {
            inputs.AddRange(parms.Select(x => new StepIO(x.ParameterType)));
        }

        return new Step(
            self: new(type),
            outputs: outputs.Distinct().ToList(),
            inputs: inputs.Distinct().ToList(),
            mutexes: mutexes.Distinct().ToList());
    }

    private static List<StepIO> GetIO<T>(IEnumerable<Attribute> attributes)
    {
        return attributes
            .OfType<T>()
            .Select(x => x switch
            {
                InputAttribute input => input.Input,
                OutputAttribute output => output.Output,
                MutuallyExclusiveAttribute mutex => mutex.Mutex,
                _ => throw new NotSupportedException("Unsupported IO type")
            })
            .Distinct()
            .ToList();
    }
}
