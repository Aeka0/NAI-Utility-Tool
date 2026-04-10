using System;
using System.Threading.Tasks;
using SkiaSharp;

namespace NAITool.Services;

public static class ImageScrambleService
{
    public enum ProcessType
    {
        Encrypt,
        Decrypt
    }

    public static SKBitmap Process(SKBitmap source, ProcessType processType, double key = 1.0)
    {
        int width = source.Width;
        int height = source.Height;
        int pixelCount = width * height;
        int offset = (int)Math.Round((Math.Sqrt(5) - 1) / 2 * pixelCount * key);

        var skPixels = source.Pixels;
        int[] pixels = new int[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            pixels[i] = (int)(uint)skPixels[i];
        }

        int[] positions = new int[pixelCount];
        int pos = 0;

        void Generate2D(int x, int y, int ax, int ay, int bx, int by)
        {
            int w = Math.Abs(ax + ay);
            int h = Math.Abs(bx + by);
            int dax = Math.Sign(ax);
            int day = Math.Sign(ay);
            int dbx = Math.Sign(bx);
            int dby = Math.Sign(by);

            if (h == 1)
            {
                for (int i = 0; i < w; ++i)
                {
                    positions[pos] = x + y * width;
                    pos++;
                    x += dax;
                    y += day;
                }
                return;
            }

            if (w == 1)
            {
                for (int i = 0; i < h; ++i)
                {
                    positions[pos] = x + y * width;
                    pos++;
                    x += dbx;
                    y += dby;
                }
                return;
            }

            int ax2 = (int)Math.Floor(ax / 2.0);
            int ay2 = (int)Math.Floor(ay / 2.0);
            int bx2 = (int)Math.Floor(bx / 2.0);
            int by2 = (int)Math.Floor(by / 2.0);
            int w2 = Math.Abs(ax2 + ay2);
            int h2 = Math.Abs(bx2 + by2);

            if (2 * w > 3 * h)
            {
                if ((w2 & 1) == 1 && w > 2)
                {
                    ax2 += dax;
                    ay2 += day;
                }
                Generate2D(x, y, ax2, ay2, bx, by);
                Generate2D(x + ax2, y + ay2, ax - ax2, ay - ay2, bx, by);
            }
            else
            {
                if ((h2 & 1) == 1 && h > 2)
                {
                    bx2 += dbx;
                    by2 += dby;
                }
                Generate2D(x, y, bx2, by2, ax2, ay2);
                Generate2D(x + bx2, y + by2, ax, ay, bx - bx2, by - by2);
                Generate2D(x + (ax - dax) + (bx2 - dbx), y + (ay - day) + (by2 - dby), -bx2, -by2, -(ax - ax2), -(ay - ay2));
            }
        }

        if (width >= height)
        {
            Generate2D(0, 0, width, 0, 0, height);
        }
        else
        {
            Generate2D(0, 0, 0, height, width, 0);
        }

        int loopPosition = pixelCount - offset;
        int[] newPixels = new int[pixelCount];

        if (pixelCount > 10000)
        {
            int taskCount = Environment.ProcessorCount;
            int step = (int)Math.Ceiling((double)pixelCount / taskCount);

            Parallel.For(0, taskCount, i =>
            {
                int begin = step * i;
                int end = Math.Min(begin + step, pixelCount);
                if (begin >= end) return;

                if (processType == ProcessType.Encrypt)
                {
                    if (begin >= loopPosition)
                    {
                        for (int j = begin; j < end; ++j)
                        {
                            newPixels[positions[j - loopPosition]] = pixels[positions[j]];
                        }
                    }
                    else if (end <= loopPosition)
                    {
                        for (int j = begin; j < end; ++j)
                        {
                            newPixels[positions[j + offset]] = pixels[positions[j]];
                        }
                    }
                    else
                    {
                        for (int j = begin; j < loopPosition; ++j)
                        {
                            newPixels[positions[j + offset]] = pixels[positions[j]];
                        }
                        for (int j = loopPosition; j < end; ++j)
                        {
                            newPixels[positions[j - loopPosition]] = pixels[positions[j]];
                        }
                    }
                }
                else
                {
                    if (begin >= loopPosition)
                    {
                        for (int j = begin; j < end; ++j)
                        {
                            newPixels[positions[j]] = pixels[positions[j - loopPosition]];
                        }
                    }
                    else if (end <= loopPosition)
                    {
                        for (int j = begin; j < end; ++j)
                        {
                            newPixels[positions[j]] = pixels[positions[j + offset]];
                        }
                    }
                    else
                    {
                        for (int j = begin; j < loopPosition; ++j)
                        {
                            newPixels[positions[j]] = pixels[positions[j + offset]];
                        }
                        for (int j = loopPosition; j < end; ++j)
                        {
                            newPixels[positions[j]] = pixels[positions[j - loopPosition]];
                        }
                    }
                }
            });
        }
        else
        {
            if (processType == ProcessType.Encrypt)
            {
                for (int i = 0; i < loopPosition; ++i)
                {
                    newPixels[positions[i + offset]] = pixels[positions[i]];
                }
                for (int i = loopPosition; i < pixelCount; ++i)
                {
                    newPixels[positions[i - loopPosition]] = pixels[positions[i]];
                }
            }
            else
            {
                for (int i = 0; i < loopPosition; ++i)
                {
                    newPixels[positions[i]] = pixels[positions[i + offset]];
                }
                for (int i = loopPosition; i < pixelCount; ++i)
                {
                    newPixels[positions[i]] = pixels[positions[i - loopPosition]];
                }
            }
        }

        var resultBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var newSkPixels = new SKColor[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            newSkPixels[i] = (uint)newPixels[i];
        }
        resultBitmap.Pixels = newSkPixels;

        return resultBitmap;
    }
}
