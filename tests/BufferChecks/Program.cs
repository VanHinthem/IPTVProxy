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

var packets=new byte[3*188+7];
for(int i=0;i<3;i++)packets[7+i*188]=0x47;
Check(SharedStreamBuffer.FindSync(packets)==7,"TS alignment after prefix");
Check(SharedStreamBuffer.FindSync(packets.AsSpan(0,380))==-1,"require third sync");
var decoder=new SharedStreamBuffer(188*8);
byte[] Ts(int pid,bool pat=false,bool random=false)
{
    var p=new byte[188];p[0]=0x47;p[1]=(byte)((pid>>8)|(pat?0x40:0));p[2]=(byte)pid;p[3]=0x30;p[4]=1;p[5]=(byte)(random?0x40:0);return p;
}
decoder.Append(Ts(100));decoder.Append(Ts(0,true));decoder.Append(Ts(100,false,true));decoder.Append(Ts(100));
Check(decoder.DecoderStartPosition(188*8)==188,"prefer PAT before random access");
Check(decoder.DecoderStartPosition(188)==564,"bounded fallback without PAT");
Console.WriteLine("TS startup checks passed.");
