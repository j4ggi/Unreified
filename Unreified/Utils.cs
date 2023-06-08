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
}
