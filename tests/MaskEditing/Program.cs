using System.Numerics;
using NAITool.Commands;
using NAITool.Models;

int checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception(scenario);
}

const int width = 5, height = 4;
var mask = new byte[width * height * 4];
// Distinct, partially transparent pixels catch row wrapping and lost antialiasing.
for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(i + 1);
var original = mask.ToArray();
foreach (int dx in new[] { int.MinValue, -6, -5, -4, -1, 0, 1, 4, 5, 6, int.MaxValue })
foreach (int dy in new[] { int.MinValue, -5, -4, -3, -1, 0, 1, 3, 4, 5, int.MaxValue })
{
    var translated = MaskTranslation.Translate(mask, width, height, dx, dy);
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        long sx = (long)x - dx, sy = (long)y - dy;
        for (int channel = 0; channel < 4; channel++)
        {
            byte expected = sx >= 0 && sx < width && sy >= 0 && sy < height
                ? mask[(sy * width + sx) * 4 + channel] : (byte)0;
            Check(translated[(y * width + x) * 4 + channel] == expected,
                $"Offset ({dx}, {dy}), pixel ({x}, {y}), channel {channel}");
        }
    }
    Check(mask.SequenceEqual(original), "Moving must not mutate the undo source");
}

var history = new UndoManager();
Vector2 imageOffset = new(17, -8);
history.PushState(mask, imageOffset, width, height);
var moved = MaskTranslation.Translate(mask, width, height, 2, -1);
var undo = history.Undo(moved, imageOffset, width, height)!.Value;
Check(undo.MaskPixels.SequenceEqual(original), "Undo restores pixels clipped by the move");
Check(undo.ImageOffset == imageOffset && undo.CanvasWidth == width && undo.CanvasHeight == height,
    "Moving a mask preserves image placement and canvas dimensions");
var redo = history.Redo(undo.MaskPixels, undo.ImageOffset, width, height)!.Value;
Check(redo.MaskPixels.SequenceEqual(moved), "Redo restores the translated mask");
history.Undo(moved, imageOffset, width, height);
history.PushState(original, imageOffset, width, height);
Check(!history.CanRedo, "A new mask edit invalidates redo");

try
{
    MaskTranslation.Translate(mask, width + 1, height, 0, 0);
    throw new Exception("Invalid mask dimensions were accepted");
}
catch (ArgumentException) { checks++; }

Console.WriteLine($"Mask editing: {checks} checks passed.");
