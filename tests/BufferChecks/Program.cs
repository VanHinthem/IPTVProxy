using IPTVProxy.Providers;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
static byte[] Packet(byte value) => Enumerable.Repeat(value, 188).ToArray();

var ring = new SharedStreamBuffer(188 * 3);
var output = new byte[188 * 2];
var empty = ring.Read(0, output);
Check(empty.Count == 0 && !empty.Changed.IsCompleted, "Empty buffer waits");
ring.Append(Packet(1));
Check(empty.Changed.IsCompleted, "Append signals reader");
var fast = ring.Read(0, output);
Check(fast.Count == 188 && fast.Next == 188 && output[0] == 1, "First reader");
ring.Append(Packet(2));
ring.Append(Packet(3));
ring.Append(Packet(4));
var slow = ring.Read(0, output);
Check(slow.Skipped == 188 && slow.Count == 376 && slow.Next == 564, "Slow reader catches up");
Check(output[0] == 2 && output[188] == 3, "Wrapped data correct");
var independent = ring.Read(fast.Next, output);
Check(independent.Skipped == 0 && independent.Next == slow.Next, "Independent reader unaffected");
var tail = ring.Read(slow.Next, output);
Check(tail.Count == 188 && output[0] == 4, "Tail packet");
Check(ring.StartPosition(188 * 2) == 376, "Recent history");
var wait = ring.Read(tail.Next, output);
ring.Complete();
Check(wait.Changed.IsCompleted && ring.Read(tail.Next, output).Completed, "Completion wakes readers");
Check(ring.Read(188, output).Count == 376, "Buffered data remains readable");
Console.WriteLine("Shared stream buffer checks passed.");
