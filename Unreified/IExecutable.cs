namespace Unreified;

public interface IExecutable<T> : IExecutable
{
    new Task<T> Execute(CancellationToken token);

#if NET6_0_OR_GREATER
    async Task<object?> IExecutable.Execute(CancellationToken token) => await Execute(token);
#endif
}

public interface IExecutable
{
    Task<object?> Execute(CancellationToken token);
}