namespace Unreified;
internal static class Utils
{
    public static bool TryPop<T>(this Stack<T> stack, out T? value)
    {
        if (stack.Count == 0)
        {
            value = default;
            return false;
        }

        value = stack.Pop();
        return true;
    }

    public static List<TOut> SelectList<TIn, TOut>(this ICollection<TIn> coll, Func<TIn, TOut> transform)
    {
        var res = new List<TOut>(coll.Count);
        foreach (var item in coll)
        {
            res.Add(transform(item));
        }
        return res;
    }

    public static bool TryAdd<TKey, TVal>(this IDictionary<TKey, TVal> dict, TKey key, TVal value)
    {
        if (dict.ContainsKey(key))
            return false;
        dict[key] = value;
        return true;
    }

    public static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dict, TKey key)
    {
        _ = dict.TryGetValue(key, out var value);
        return value!;
    }

    public static bool IsAssignableTo(this Type type1, Type type2)
    {
        return type2.IsAssignableFrom(type1);
    }

    public static async ValueTask<ConditionalLock> WaitIf(this SemaphoreSlim semaphore, bool condition)
    {
        if (condition)
            await semaphore.WaitAsync();
        return new ConditionalLock(semaphore, condition);
    }

    public static async ValueTask<ConditionalLock> LockAsync(this SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        return new ConditionalLock(semaphore, true);
    }

    public static ConditionalLock Lock(this SemaphoreSlim semaphore)
    {
        semaphore.Wait();
        return new ConditionalLock(semaphore, true);
    }

    public readonly record struct ConditionalLock : IDisposable
    {
        private readonly SemaphoreSlim semaphore;
        private readonly bool doRelease;

        public ConditionalLock(SemaphoreSlim semaphore, bool doRelease)
        {
            this.semaphore = semaphore;
            this.doRelease = doRelease;
        }
        public readonly void Dispose()
        {
            if (doRelease)
                semaphore.Release();
        }
    }
}
