using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lokad.Bash.Tests;

/// <summary>Measures the material array payload of the pinned runtime's indexed collections.</summary>
internal static class CollectionBacking
{
    internal static long ArrayPayloadBytes(object collection)
    {
        var type = collection.GetType();
        var entries = Assert.IsAssignableFrom<Array>(
            type.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(collection));
        var buckets = Assert.IsType<int[]>(
            type.GetField("_buckets", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(collection));
        var entryType = entries.GetType().GetElementType() ?? throw new InvalidOperationException();
        var entrySize = Assert.IsType<int>((typeof(CollectionBacking)
            .GetMethod(nameof(ElementSize), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException()).MakeGenericMethod(entryType).Invoke(null, null));
        return (long)entries.Length * entrySize + sizeof(int) * (long)buckets.Length;
    }

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();
}
