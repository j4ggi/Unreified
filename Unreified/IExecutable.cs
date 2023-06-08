namespace Unreified;

public interface IExecutable<T> : IExecutable
{
    new Task<T> Execute(CancellationToken token);
}

public interface IExecutable
{
    Task<object> Execute(CancellationToken token);
}