using Microsoft.Extensions.ObjectPool;

using System.Collections.Concurrent;

namespace Unreified;

public struct PooledList<T> : IDisposable
{
    private static readonly ObjectPool<List<T>> ListPool = ObjectPool.Create(new Policy());
    private static readonly ConcurrentDictionary<int, ConcurrentBag<T[]>> ArrayPool = new();

    public readonly List<T> List { get; }
    private T[]? array;

    private PooledList(List<T> list)
    {
        List = list;
        array = null;
    }

    public static PooledList<T> Get() => new(ListPool.Get());

    public readonly void Dispose()
    {
        if (array is not null)
        {
            Array.Clear(array, 0, array.Length);
            if (ArrayPool.TryGetValue(array.Length, out var pool))
                pool.Add(array);
        }

        ListPool.Return(List);
    }

    /// <summary>
    /// Get an array that will be returned to an object pool when this <see cref="PooledList{T}"/> is being disposed
    /// </summary>
    public T[] ToDisposableArray()
    {
        var bag = ArrayPool.GetOrAdd(List.Count, count => new ConcurrentBag<T[]>());
        if (bag.TryTake(out var result))
        {
            array = result;
            List.CopyTo(result);
            return result;
        }
        else
        {
            array = new T[List.Count];
            List.CopyTo(array);
            return array;
        }
    }

    private class Policy : IPooledObjectPolicy<List<T>>
    {
        public List<T> Create() => new();
        public bool Return(List<T> obj)
        {
            obj.Clear();
            return true;
        }
    }
}

public readonly struct Pooled<T> : IAsyncDisposable where T : class, IAsyncDisposable, new()
{
    private static readonly ObjectPool<T> pool = ObjectPool.Create<T>();

    public readonly T Value { get; }

    private Pooled(T list)
    {
        Value = list;
    }

    public static Pooled<T> Get() => new(pool.Get());

    public async ValueTask DisposeAsync()
    {
        await Value.DisposeAsync();
        pool.Return(Value);
    }
}
