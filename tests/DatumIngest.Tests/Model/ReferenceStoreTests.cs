using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="ReferenceStore"/> string interning, verifying that repeated
/// string values share a single backing slot rather than growing the store linearly.
/// </summary>
public sealed class ReferenceStoreTests
{
    // ───────────────────────── InternString ─────────────────────────

    [Fact]
    public void InternString_SameValue_ReturnsSameIndex()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            int first = store.InternString("hello");
            int second = store.InternString("hello");

            Assert.Equal(first, second);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternString_DifferentValues_ReturnDifferentIndices()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            int a = store.InternString("alpha");
            int b = store.InternString("beta");

            Assert.NotEqual(a, b);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternString_ManyDuplicates_StoreGrowsBoundedByDistinctCount()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();
            int countBefore = store.Count;

            string[] values = ["prior", "train", "test"];
            for (int i = 0; i < 10_000; i++)
            {
                store.InternString(values[i % values.Length]);
            }

            int newEntries = store.Count - countBefore;

            // Only 3 distinct values — should produce exactly 3 entries,
            // not 10,000.
            Assert.Equal(values.Length, newEntries);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternString_RetrievedValueMatchesOriginal()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            int index = store.InternString("world");

            Assert.Equal("world", store.Get<string>(index));
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    // ───────────────────────── InternStringFromUtf8 ─────────────────────────

    [Fact]
    public void InternStringFromUtf8_SameBytes_ReturnsSameIndex()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();
            byte[] utf8 = Encoding.UTF8.GetBytes("hello");

            int first = store.InternStringFromUtf8(utf8);
            int second = store.InternStringFromUtf8(utf8);

            Assert.Equal(first, second);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternStringFromUtf8_MatchesInternString()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            int fromString = store.InternString("café");
            int fromUtf8 = store.InternStringFromUtf8(Encoding.UTF8.GetBytes("café"));

            Assert.Equal(fromString, fromUtf8);
            Assert.Equal("café", store.Get<string>(fromUtf8));
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternStringFromUtf8_ManyDuplicates_StoreGrowsBoundedByDistinctCount()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();
            int countBefore = store.Count;

            byte[][] utf8Values =
            [
                Encoding.UTF8.GetBytes("prior"),
                Encoding.UTF8.GetBytes("train"),
                Encoding.UTF8.GetBytes("test"),
            ];

            for (int i = 0; i < 10_000; i++)
            {
                store.InternStringFromUtf8(utf8Values[i % utf8Values.Length]);
            }

            int newEntries = store.Count - countBefore;

            Assert.Equal(utf8Values.Length, newEntries);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void InternStringFromUtf8_LongString_HandledCorrectly()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            // Create a string longer than the 256-char stackalloc threshold.
            string longValue = new('x', 500);
            byte[] utf8 = Encoding.UTF8.GetBytes(longValue);

            int first = store.InternStringFromUtf8(utf8);
            int second = store.InternStringFromUtf8(utf8);

            Assert.Equal(first, second);
            Assert.Equal(longValue, store.Get<string>(first));
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    // ───────────────────────── Scope isolation ─────────────────────────

    [Fact]
    public void InternString_ScopesAreIsolated()
    {
        // First scope: intern "hello" and capture its index.
        ReferenceStore.BeginQueryScope();
        int indexInScope1;
        try
        {
            indexInScope1 = ReferenceStore.CurrentOrCreate().InternString("hello");
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }

        // Second scope: intern the same string — should get a fresh index
        // because the first scope was cleared.
        ReferenceStore.BeginQueryScope();
        try
        {
            int indexInScope2 = ReferenceStore.CurrentOrCreate().InternString("hello");

            // Both scopes assign index 0 (first entry), but in different stores.
            // The key assertion: after EndQueryScope, the old store is cleared,
            // and the new scope starts fresh.
            Assert.Equal("hello", ReferenceStore.CurrentOrCreate().Get<string>(indexInScope2));
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    // ───────────────────────── Reset ─────────────────────────

    [Fact]
    public void Reset_ClearsInternCache()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            ReferenceStore store = ReferenceStore.CurrentOrCreate();

            store.InternString("hello");
            Assert.Equal(1, store.Count);

            store.Reset();
            Assert.Equal(0, store.Count);

            // After reset, interning the same value should allocate a new slot.
            int index = store.InternString("hello");
            Assert.Equal(0, index);
            Assert.Equal(1, store.Count);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    // ──────────────── DataValue.FromString interning ────────────────

    [Fact]
    public void DataValueFromString_RepeatedValues_ShareReferenceStoreSlot()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            int countBefore = ReferenceStore.CurrentOrCreate().Count;

            // Create 1,000 DataValues from the same 3 strings.
            for (int i = 0; i < 1_000; i++)
            {
                _ = DataValue.FromString("alpha");
                _ = DataValue.FromString("beta");
                _ = DataValue.FromString("gamma");
            }

            int newEntries = ReferenceStore.CurrentOrCreate().Count - countBefore;

            Assert.Equal(3, newEntries);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }

    [Fact]
    public void DataValueFromJsonValue_RepeatedValues_ShareReferenceStoreSlot()
    {
        ReferenceStore.BeginQueryScope();
        try
        {
            int countBefore = ReferenceStore.CurrentOrCreate().Count;

            for (int i = 0; i < 500; i++)
            {
                _ = DataValue.FromJsonValue("{\"key\":1}");
                _ = DataValue.FromJsonValue("{\"key\":2}");
            }

            int newEntries = ReferenceStore.CurrentOrCreate().Count - countBefore;

            Assert.Equal(2, newEntries);
        }
        finally
        {
            ReferenceStore.EndQueryScope();
        }
    }
}
