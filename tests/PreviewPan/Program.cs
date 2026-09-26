using System.Diagnostics;
using NAITool.Models;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}

var pan = new PreviewPanState();
pan.Move(10, 20);
Check(!pan.TryTakeOffsets(1000, 1000, out _, out _), "Ignore moves before capture");
pan.Begin(100, 100, 500, 500);
pan.Move(110, 90);
pan.Move(130, 70);
Check(pan.TryTakeOffsets(1000, 1000, out var x, out var y) && x == 470 && y == 530,
    "Use the latest viewport-space position without losing any displacement");
Check(!pan.TryTakeOffsets(1000, 1000, out _, out _), "Idle frames do no scroll work");
pan.Move(130, 70);
Check(!pan.TryTakeOffsets(1000, 1000, out _, out _), "Repeated positions do no scroll work");
pan.Move(10000, -10000);
Check(pan.TryTakeOffsets(1000, 1000, out x, out y) && x == 0 && y == 1000, "Clamp to scroll bounds");
pan.Move(10001, -10001);
Check(!pan.TryTakeOffsets(1000, 1000, out _, out _), "Pushing against the same edge does no scroll work");
pan.Move(90, 110);
Check(pan.TryTakeOffsets(1000, 1000, out x, out y) && x == 510 && y == 490,
    "Returning from the edge still uses the original drag anchor");
pan.Move(double.NaN, double.PositiveInfinity);
Check(!pan.TryTakeOffsets(1000, 1000, out _, out _), "Invalid coordinates do not corrupt the session");
pan.Move(0, 0);
pan.End();
Check(!pan.IsActive && !pan.TryTakeOffsets(1000, 1000, out _, out _), "Cancel drops pending work");
pan.Begin(0, 0, 0, 0);
pan.Move(-100, -100);
Check(!pan.TryTakeOffsets(0, 0, out _, out _), "Images that fit the viewport do not issue redundant scroll requests");
pan.Begin(0.25, 0.75, 400.5, 600.5);
pan.Move(1.75, 2.25);
Check(pan.TryTakeOffsets(1000, 1000, out x, out y) && x == 399 && y == 599,
    "Preserve fractional DIP coordinates at high DPI");

// Replay high-rate mouse traces against different display rates. This measures the
// request coalescing path, not WinUI presentation FPS or GPU rendering performance.
foreach (int inputHz in new[] { 125, 1000, 8000 })
foreach (int frameHz in new[] { 60, 120, 144, 240 })
{
    pan.Begin(0, 0, 10000, 10000);
    int commits = 0;
    int nextFrame = 1;
    for (int sample = 1; sample <= inputHz; sample++)
    {
        double position = sample * 1000.0 / inputHz;
        pan.Move(position, -position);
        if (sample * frameHz >= nextFrame * inputHz)
        {
            if (pan.TryTakeOffsets(20000, 20000, out x, out y)) commits++;
            nextFrame = sample * frameHz / inputHz + 1;
        }
    }
    if (pan.TryTakeOffsets(20000, 20000, out x, out y)) commits++;
    Check(commits <= Math.Min(inputHz, frameHz), "At most one scroll request per simulated frame");
    pan.Move(1000.25, -1000.25); // Release between frames must commit the final location.
    Check(pan.TryTakeOffsets(20000, 20000, out x, out y) && x == 8999.75 && y == 11000.25,
        "Final release flush preserves exact position");
    pan.End();
    Console.WriteLine($"{inputHz} input events/s, {frameHz} frames/s: {commits} scroll requests (+ final release flush)");
}

pan.Begin(0, 0, 10000, 10000);
var watch = Stopwatch.StartNew();
long before = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 1_000_000; i++)
{
    pan.Move(i % 1000, -(i % 1000));
    if (i % 32 == 0) pan.TryTakeOffsets(20000, 20000, out _, out _);
}
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
watch.Stop();
Check(allocated == 0, "Coalescing must not allocate per pointer event or frame");
Console.WriteLine($"1,000,000 synthetic moves: {watch.Elapsed.TotalMilliseconds:F2} ms, {allocated} allocated bytes");
Console.WriteLine($"Passed {checks} preview pan checks.");
