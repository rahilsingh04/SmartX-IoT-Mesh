using System.Collections;

namespace SmartX.Api.Models;

/// <summary>
/// COLLECTIONS OPTIMISATION (custom data structure).
///
/// A fixed-capacity circular ("ring") buffer used instead of a plain List&lt;T&gt;.
/// A List&lt;T&gt; grows without bound and needs RemoveAt(0) (an O(n) shift) to keep a
/// rolling window. TelemetryRingBuffer&lt;T&gt;:
///   - allocates its memory once (no re-allocation while telemetry keeps arriving),
///   - performs Add() in O(1) by overwriting the oldest slot once full,
///   - enumerates oldest -> newest so every LINQ query (TakeLast, Where, ...) works.
///
/// Used by: the gateway reading log, each sensor's recent-values window, the alert
/// log and the processing-order log (Part 2).
/// </summary>
public class TelemetryRingBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _start;

    public int Capacity { get; }
    public int Count { get; private set; }

    public TelemetryRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _buffer = new T[capacity];
    }

    /// <summary>O(1): writes into the next slot, overwriting the oldest entry once full.</summary>
    public void Add(T item)
    {
        var writeIndex = (_start + Count) % Capacity;
        _buffer[writeIndex] = item;

        if (Count < Capacity)
        {
            Count++;
        }
        else
        {
            // Buffer full: the slot just overwritten was the oldest entry.
            _start = (_start + 1) % Capacity;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return _buffer[(_start + i) % Capacity];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
