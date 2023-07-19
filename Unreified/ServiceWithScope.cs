namespace Unreified;

/// <summary>
/// Wrapper on <paramref name="Scope"/> and <paramref name="Service"/>.
/// <para />
/// <paramref name="Scope"/> contains disposable dependencies for the <paramref name="Service"/> and <paramref name="Service"/> the service itself
/// if it is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>.
/// <para />
/// Disposing this is equivalent to disposing <paramref name="Scope"/>.
/// If <paramref name="Service"/> is <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/>, it will also be disposed.
/// </summary>
public record struct ServiceWithScope<T>(IAsyncDisposable Scope, T Service) : IAsyncDisposable
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public async readonly ValueTask DisposeAsync()
    {
        await Scope.DisposeAsync();
    }

    public static implicit operator (IAsyncDisposable Scope, T Service)(ServiceWithScope<T> value)
    {
        return (value.Scope, value.Service);
    }

    public static implicit operator ServiceWithScope<T>((IAsyncDisposable Scope, T Service) value)
    {
        return new ServiceWithScope<T>(value.Scope, value.Service);
    }
}