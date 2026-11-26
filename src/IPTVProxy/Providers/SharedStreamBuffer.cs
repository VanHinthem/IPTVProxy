namespace IPTVProxy.Providers;

// Single producer, independent readers. All positions are absolute byte offsets.
public sealed class SharedStreamBuffer
{
    private readonly object gate = new();
    private readonly byte[] data;
    private long head;
    private bool completed;
    private TaskCompletionSource changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SharedStreamBuffer(int capacity)
    {
        if (capacity < 188 || capacity % 188 != 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        data = new byte[capacity];
    }
    // Require three sync bytes at packet intervals before accepting alignment.
    public static int FindSync(ReadOnlySpan<byte> bytes)
    {
        for(int i=0;i+376<bytes.Length;i++)
            if(bytes[i]==0x47&&bytes[i+188]==0x47&&bytes[i+376]==0x47)return i;
        return -1;
    }
    // A recent PAT preceding a random-access indication is a best-effort
    // decoder entry point, not a guarantee of a complete IDR or PMT.
    public long DecoderStartPosition(int historyBytes)
    {
        lock(gate)
        {
            var fallback=Math.Max(0,head-Math.Min(Math.Max(0,historyBytes/188*188),data.Length));
            long pat=-1,preferred=-1;
            for(long pos=fallback;pos<head;pos+=188)
            {
                var i=(int)(pos%data.Length);
                if(data[i]!=0x47||(data[i+1]&0x80)!=0)continue;
                var pid=((data[i+1]&31)<<8)|data[i+2];
                if(pid==0&&(data[i+1]&0x40)!=0)pat=pos;
                var adaptation=(data[i+3]>>4)&3;
                if(pat>=0&&pid!=0&&pid!=8191&&(adaptation==2||adaptation==3)&&data[i+4]>0&&data[i+4]<=183&&(data[i+5]&0x40)!=0)preferred=pat;
            }
            return preferred>=0?preferred:pat>=0?pat:fallback;
        }
    }
    public long StartPosition(int historyBytes)
    {
        lock (gate)
        {
            var history = Math.Min(Math.Max(0, historyBytes / 188 * 188), data.Length);
            return Math.Max(0, head - history);
        }
    }
    public void Append(ReadOnlySpan<byte> source)
    {
        if (source.Length % 188 != 0 || source.Length > data.Length) throw new ArgumentException("Invalid TS block.");
        if (source.IsEmpty) return;
        lock (gate)
        {
            if (completed) return;
            int offset = (int)(head % data.Length);
            int first = Math.Min(source.Length, data.Length - offset);
            source[..first].CopyTo(data.AsSpan(offset));
            source[first..].CopyTo(data);
            head += source.Length;
            var signal = changed;
            changed = NewSignal();
            signal.TrySetResult();
        }
    }
    public (int Count, long Next, long Skipped, bool Completed, Task Changed) Read(long cursor, Span<byte> destination)
    {
        lock (gate)
        {
            var oldest = Math.Max(0, head - data.Length);
            var skipped = Math.Max(0, oldest - cursor);
            cursor = Math.Max(cursor, oldest);
            int count = (int)Math.Min(head - cursor, destination.Length / 188 * 188);
            int offset = (int)(cursor % data.Length);
            int first = Math.Min(count, data.Length - offset);
            data.AsSpan(offset, first).CopyTo(destination);
            data.AsSpan(0, count - first).CopyTo(destination[first..]);
            return (count, cursor + count, skipped, completed, changed.Task);
        }
    }
    public void Complete()
    {
        lock (gate)
        {
            completed = true;
            changed.TrySetResult();
        }
    }
}
