namespace WinState.App;

internal static class CollectionExtensions
{
    public static void AddRange<T>(this ICollection<T> target, IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
