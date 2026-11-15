using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClaudeStudioExtension.Agent;

// The agent's output lines, for one reader at a time: an unbounded queue with
// the part of System.Threading.Channels used here (not in .NET Framework, and
// a VSIX copy of it could clash with the one Visual Studio loads).
internal sealed class LineQueue
{
    private readonly Queue<string> _items = new();
    // Completed by every write and by Complete; a waiter that finds it completed
    // with nothing queued replaces it.
    private TaskCompletionSource<bool> _signal = NewSignal();
    private bool _completed;

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Write(string line)
    {
        TaskCompletionSource<bool> signal;
        lock (_items)
        {
            if (_completed) return;
            _items.Enqueue(line);
            signal = _signal;
        }
        signal.TrySetResult(true);
    }

    // No more lines once the queued ones are read.
    public void Complete()
    {
        TaskCompletionSource<bool> signal;
        lock (_items)
        {
            _completed = true;
            signal = _signal;
        }
        signal.TrySetResult(true);
    }

    public bool TryRead(out string line)
    {
        lock (_items)
        {
            if (_items.Count > 0)
            {
                line = _items.Dequeue();
                return true;
            }
        }
        line = "";
        return false;
    }

    // True when a line is there to read (another reader may still take it);
    // false once complete and drained.
    public async Task<bool> WaitToReadAsync()
    {
        while (true)
        {
            Task wait;
            lock (_items)
            {
                if (_items.Count > 0) return true;
                if (_completed) return false;
                if (_signal.Task.IsCompleted) _signal = NewSignal();
                wait = _signal.Task;
            }
            await wait;
        }
    }

    // The next line; null once complete and drained.
    public async Task<string?> ReadAsync()
    {
        while (await WaitToReadAsync())
            if (TryRead(out var line)) return line;
        return null;
    }
}
