﻿namespace Unreified;
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
        return value;
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
