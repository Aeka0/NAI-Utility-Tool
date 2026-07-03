using ComputeSharp;
using ComputeSharp.D2D1;

namespace NAITool.Rendering;

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct BrightnessContrastShader : ID2D1PixelShader
{
    private readonly float brightness;
    private readonly float contrast;

    public BrightnessContrastShader(float brightness, float contrast)
    {
        this.brightness = brightness;
        this.contrast = contrast;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float3 rgb = (color.RGB - 0.5f) * this.contrast + 0.5f + this.brightness;
        return new(Hlsl.Saturate(rgb), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct SaturationVibranceShader : ID2D1PixelShader
{
    private readonly float saturation;
    private readonly float vibrance;

    public SaturationVibranceShader(float saturation, float vibrance)
    {
        this.saturation = saturation;
        this.vibrance = vibrance;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float3 rgb = color.RGB;
        float gray = 0.299f * rgb.X + 0.587f * rgb.Y + 0.114f * rgb.Z;
        rgb = gray + (rgb - gray) * this.saturation;

        float max = Hlsl.Max(rgb.X, Hlsl.Max(rgb.Y, rgb.Z));
        float avg = (rgb.X + rgb.Y + rgb.Z) / 3f;
        float amount = this.vibrance * (1f - Hlsl.Abs(max - avg));
        rgb += (rgb - avg) * amount;

        return new(Hlsl.Saturate(rgb), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct GlowExtractShader : ID2D1PixelShader
{
    private readonly float threshold;

    public GlowExtractShader(float threshold)
    {
        this.threshold = threshold;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float maxColor = Hlsl.Max(color.R, Hlsl.Max(color.G, color.B));
        float knee = Hlsl.Max(1f / 255f, this.threshold * 0.15f);
        float soft = Hlsl.Clamp(maxColor - this.threshold + knee, 0f, 2f * knee);
        soft = soft * soft / (4f * knee + 0.0001f);
        float contribution = Hlsl.Max(soft, maxColor - this.threshold);
        float factor = maxColor > 0.0001f ? contribution / maxColor : 0f;

        return new(Hlsl.Saturate(color.RGB * factor), 1f);
    }
}

[D2DInputCount(2)]
[D2DInputSimple(0)]
[D2DInputSimple(1)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct GlowCompositeShader : ID2D1PixelShader
{
    private readonly float strength;
    private readonly float saturation;

    public GlowCompositeShader(float strength, float saturation)
    {
        this.strength = strength;
        this.saturation = saturation;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float4 glow = D2D.GetInput(1);
        float3 glowRgb = Hlsl.Max(glow.RGB, new float3(0f, 0f, 0f));
        float peakBefore = Hlsl.Max(glowRgb.X, Hlsl.Max(glowRgb.Y, glowRgb.Z));
        float gray = 0.299f * glowRgb.X + 0.587f * glowRgb.Y + 0.114f * glowRgb.Z;
        glowRgb = gray + (glowRgb - gray) * this.saturation;
        float peakAfter = Hlsl.Max(glowRgb.X, Hlsl.Max(glowRgb.Y, glowRgb.Z));
        if (peakBefore > 0.001f && peakAfter > 0.001f)
        {
            glowRgb *= peakBefore / peakAfter;
        }

        float3 rgb = color.RGB + Hlsl.Max(glowRgb * this.strength, new float3(0f, 0f, 0f));
        return new(Hlsl.Saturate(rgb), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct RadialBlurShader : ID2D1PixelShader
{
    private readonly float strength;
    private readonly float centerX;
    private readonly float centerY;
    private readonly int mode;
    private readonly int sampleCount;
    private readonly float width;
    private readonly float height;

    public RadialBlurShader(
        float strength,
        float centerX,
        float centerY,
        int mode,
        int sampleCount,
        float width,
        float height)
    {
        this.strength = strength;
        this.centerX = centerX;
        this.centerY = centerY;
        this.mode = mode;
        this.sampleCount = sampleCount;
        this.width = width;
        this.height = height;
    }

    public float4 Execute()
    {
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float2 delta = pos - new float2(this.centerX, this.centerY);
        float zoomRadius = 0.0025f + this.strength * 0.075f;
        float spinAngle = this.strength * 0.22f;
        float maxX = Hlsl.Max(this.centerX, this.width - 1f - this.centerX);
        float maxY = Hlsl.Max(this.centerY, this.height - 1f - this.centerY);
        float maxDist = Hlsl.Max(Hlsl.Sqrt(maxX * maxX + maxY * maxY), 1f);
        float4 accum = new(0f, 0f, 0f, 0f);
        float weightSum = 0f;

        for (int i = 0; i < 48; i++)
        {
            if (i >= this.sampleCount)
            {
                break;
            }

            float t = this.sampleCount == 1
                ? 0f
                : (i / (this.sampleCount - 1f) - 0.5f) * 2f;
            float2 samplePos;
            float weight;

            if (this.mode == 1)
            {
                float angle = t * spinAngle;
                float cos = Hlsl.Cos(angle);
                float sin = Hlsl.Sin(angle);
                samplePos = new(
                    this.centerX + delta.X * cos - delta.Y * sin,
                    this.centerY + delta.X * sin + delta.Y * cos);
                weight = 1f - Hlsl.Abs(t) * 0.5f;
            }
            else if (this.mode == 2)
            {
                float distNorm = Hlsl.Sqrt(delta.X * delta.X + delta.Y * delta.Y) / maxDist;
                float localRadius = distNorm * (0.5f + this.strength * 14f);
                if (localRadius < 0.75f)
                {
                    samplePos = pos;
                }
                else
                {
                    float angleStep = 6.28318530718f / this.sampleCount;
                    float angle = i * angleStep;
                    samplePos = pos + new float2(Hlsl.Cos(angle), Hlsl.Sin(angle)) * localRadius;
                }
                weight = 1f;
            }
            else
            {
                float scale = t * zoomRadius;
                samplePos = pos - delta * scale;
                weight = 1f;
            }

            accum += D2D.SampleInputAtPosition(0, samplePos) * weight;
            weightSum += weight;
        }

        return weightSum > 0.0001f ? accum / weightSum : D2D.SampleInputAtPosition(0, pos);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct TemperatureShader : ID2D1PixelShader
{
    private readonly float delta;
    private readonly float tintDelta;

    public TemperatureShader(float delta, float tintDelta)
    {
        this.delta = delta;
        this.tintDelta = tintDelta;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float3 offset = new(
            this.delta + this.tintDelta * 0.55f,
            this.delta * 0.15f - this.tintDelta,
            -this.delta + this.tintDelta * 0.55f);

        return new(Hlsl.Saturate(color.RGB + offset), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct VignetteShader : ID2D1PixelShader
{
    private readonly float strength;
    private readonly float softness;
    private readonly float width;
    private readonly float height;

    public VignetteShader(float strength, float softness, float width, float height)
    {
        this.strength = strength;
        this.softness = softness;
        this.width = width;
        this.height = height;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float cx = (this.width - 1f) * 0.5f;
        float cy = (this.height - 1f) * 0.5f;
        float dx = pos.X - cx;
        float dy = pos.Y - cy;
        float maxDist = Hlsl.Sqrt(cx * cx + cy * cy);
        float dist = maxDist > 0.0001f ? Hlsl.Sqrt(dx * dx + dy * dy) / maxDist : 0f;
        float start = Hlsl.Clamp(1f - this.softness, 0.05f, 0.95f);
        float t = Hlsl.Clamp((dist - start) / Hlsl.Max(this.softness, 0.001f), 0f, 1f);
        float factor = 1f - this.strength * t * t;

        return new(Hlsl.Saturate(color.RGB * factor), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct ChromaticAberrationShader : ID2D1PixelShader
{
    private readonly float shift;
    private readonly int colorPair;
    private readonly int sampleCount;
    private readonly float width;
    private readonly float height;

    public ChromaticAberrationShader(float shift, int colorPair, int sampleCount, float width, float height)
    {
        this.shift = shift;
        this.colorPair = colorPair;
        this.sampleCount = sampleCount;
        this.width = width;
        this.height = height;
    }

    public float4 Execute()
    {
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float cx = (this.width - 1f) * 0.5f;
        float cy = (this.height - 1f) * 0.5f;
        float dx = pos.X - cx;
        float dy = pos.Y - cy;
        float len = Hlsl.Sqrt(dx * dx + dy * dy);
        float2 unit = len > 0.001f ? new float2(dx / len, dy / len) : new float2(0f, 0f);

        float redTarget = 1f;
        float greenTarget = 0f;
        float blueTarget = -1f;
        if (this.colorPair == 1)
        {
            redTarget = -1f;
            greenTarget = 1f;
            blueTarget = 1f;
        }
        else if (this.colorPair == 2)
        {
            redTarget = -1f;
            greenTarget = 1f;
            blueTarget = -1f;
        }
        else if (this.colorPair >= 3)
        {
            redTarget = 1f;
            greenTarget = 1f;
            blueTarget = -1f;
        }

        int samples = Hlsl.Clamp(this.sampleCount, 3, 16);
        float denominator = Hlsl.Max(samples - 1f, 1f);
        float bandWidth = Hlsl.Max(2.7f / denominator, 0.42f);
        float3 sum = new(0f, 0f, 0f);
        float3 total = new(0f, 0f, 0f);
        float3 fullTotal = new(0f, 0f, 0f);
        float4 center = D2D.SampleInputAtPosition(0, pos);

        for (int i = 0; i < 16; i++)
        {
            if (i < samples)
            {
                float t = -1f + 2f * i / denominator;
                float3 weights = new(
                    SmoothBandWeight(t, redTarget, bandWidth),
                    SmoothBandWeight(t, greenTarget, bandWidth),
                    SmoothBandWeight(t, blueTarget, bandWidth));
                float2 samplePos = pos + unit * this.shift * t;
                float edgeWeight = EdgeSampleWeight(samplePos);
                float3 weighted = weights * edgeWeight;
                float3 sampled = D2D.SampleInputAtPosition(0, samplePos).RGB;
                sum += sampled * weighted;
                total += weighted;
                fullTotal += weights;
            }
        }

        sum += center.RGB * Hlsl.Max(fullTotal - total, new float3(0f, 0f, 0f));
        total = Hlsl.Max(fullTotal, total);
        float3 rgb = new(
            total.X > 0.0001f ? sum.X / total.X : center.R,
            total.Y > 0.0001f ? sum.Y / total.Y : center.G,
            total.Z > 0.0001f ? sum.Z / total.Z : center.B);

        return new(Hlsl.Saturate(rgb), center.A);
    }

    private static float SmoothBandWeight(float value, float target, float bandWidth)
    {
        float weight = Hlsl.Clamp(1f - Hlsl.Abs(value - target) / bandWidth, 0f, 1f);
        return weight * weight * (3f - 2f * weight);
    }

    private float EdgeSampleWeight(float2 pos)
    {
        float xIn = SmoothStep(-1f, 0f, pos.X);
        float yIn = SmoothStep(-1f, 0f, pos.Y);
        float xOut = SmoothStep(-1f, 0f, this.width - 1f - pos.X);
        float yOut = SmoothStep(-1f, 0f, this.height - 1f - pos.Y);
        return xIn * yIn * xOut * yOut;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Hlsl.Clamp((value - edge0) / Hlsl.Max(edge1 - edge0, 0.0001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

[D2DInputCount(1)]
[D2DInputComplex(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct JpegLossShader : ID2D1PixelShader
{
    private readonly float loss;
    private readonly float iterations;
    private readonly float blockSize;
    private readonly float chromaBleed;
    private readonly float width;
    private readonly float height;

    public JpegLossShader(float loss, float iterations, float blockSize, float chromaBleed, float width, float height)
    {
        this.loss = loss;
        this.iterations = iterations;
        this.blockSize = blockSize;
        this.chromaBleed = chromaBleed;
        this.width = width;
        this.height = height;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float safeWidth = Hlsl.Max(this.width, 1f);
        float safeHeight = Hlsl.Max(this.height, 1f);
        float block = Hlsl.Max(this.blockSize, 1f);
        float lossBase = Hlsl.Max(1f - this.loss * 0.82f, 0.0001f);
        float cumulativeLoss = 1f - Hlsl.Pow(lossBase, this.iterations * 0.32f);
        float severeLoss = cumulativeLoss * cumulativeLoss;
        float2 blockOrigin = new(
            Hlsl.Floor(pos.X / block) * block,
            Hlsl.Floor(pos.Y / block) * block);
        float2 blockCenter = blockOrigin + new float2(block * 0.5f, block * 0.5f);
        blockCenter = ClampPosition(blockCenter, safeWidth, safeHeight);
        float2 blockLocal = new(
            Hlsl.Frac(pos.X / block),
            Hlsl.Frac(pos.Y / block));

        float3 rgb = color.RGB;
        float3 ycbcr = ToYcbcr(rgb);
        float3 blockAverage = new(0f, 0f, 0f);
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                float2 sampleOffset = new float2(x * block * 0.28f, y * block * 0.28f);
                blockAverage += D2D.SampleInputAtPosition(0, ClampPosition(blockCenter + sampleOffset, safeWidth, safeHeight)).RGB;
            }
        }

        blockAverage /= 9f;
        float3 blockYcbcr = ToYcbcr(blockAverage);
        float yLevels = Lerp(220f, 12f, severeLoss);
        float cLevels = Lerp(160f, 7f, severeLoss);
        ycbcr.X = Hlsl.Floor(Lerp(ycbcr.X, blockYcbcr.X, severeLoss * 0.45f) * yLevels + 0.5f) / yLevels;
        ycbcr.Y = Hlsl.Floor(Lerp(ycbcr.Y, blockYcbcr.Y, cumulativeLoss * this.chromaBleed) * cLevels + 0.5f) / cLevels;
        ycbcr.Z = Hlsl.Floor(Lerp(ycbcr.Z, blockYcbcr.Z, cumulativeLoss * this.chromaBleed) * cLevels + 0.5f) / cLevels;

        float3 compressed = FromYcbcr(ycbcr);
        float3 blurSample = (
            D2D.SampleInputAtPosition(0, ClampPosition(pos + new float2(1f, 0f), safeWidth, safeHeight)).RGB +
            D2D.SampleInputAtPosition(0, ClampPosition(pos - new float2(1f, 0f), safeWidth, safeHeight)).RGB +
            D2D.SampleInputAtPosition(0, ClampPosition(pos + new float2(0f, 1f), safeWidth, safeHeight)).RGB +
            D2D.SampleInputAtPosition(0, ClampPosition(pos - new float2(0f, 1f), safeWidth, safeHeight)).RGB) * 0.25f;
        float3 diff = rgb - blurSample;
        float edge = Hlsl.Clamp(Hlsl.Sqrt(diff.X * diff.X + diff.Y * diff.Y + diff.Z * diff.Z) * 4f, 0f, 1f);
        float ringingPattern = Hlsl.Cos((blockLocal.X - 0.5f) * 6.28318f) * Hlsl.Cos((blockLocal.Y - 0.5f) * 6.28318f);
        float blockBoundary = Hlsl.Max(
            SmoothStep(0f, 0.16f, 0.16f - Hlsl.Min(blockLocal.X, 1f - blockLocal.X)),
            SmoothStep(0f, 0.16f, 0.16f - Hlsl.Min(blockLocal.Y, 1f - blockLocal.Y)));
        float mosquito = (HashNoise(pos.X, pos.Y, this.iterations + 11f) - 0.5f) * edge * cumulativeLoss * 0.12f;
        compressed += ringingPattern * edge * cumulativeLoss * 0.06f + mosquito;
        compressed = Lerp(compressed, blockAverage, blockBoundary * severeLoss * 0.18f);
        float3 finalRgb = Lerp(rgb, compressed, Hlsl.Clamp(cumulativeLoss * 1.18f, 0f, 1f));

        return new(Hlsl.Saturate(finalRgb), color.A);
    }

    private static float2 ClampPosition(float2 pos, float width, float height)
    {
        return new(
            Hlsl.Clamp(pos.X, 0f, Hlsl.Max(width - 1f, 0f)),
            Hlsl.Clamp(pos.Y, 0f, Hlsl.Max(height - 1f, 0f)));
    }

    private static float3 ToYcbcr(float3 rgb)
    {
        float y = rgb.X * 0.299f + rgb.Y * 0.587f + rgb.Z * 0.114f;
        float cb = rgb.X * -0.168736f + rgb.Y * -0.331264f + rgb.Z * 0.5f + 0.5f;
        float cr = rgb.X * 0.5f + rgb.Y * -0.418688f + rgb.Z * -0.081312f + 0.5f;
        return new(y, cb, cr);
    }

    private static float3 FromYcbcr(float3 ycbcr)
    {
        float y = ycbcr.X;
        float cb = ycbcr.Y - 0.5f;
        float cr = ycbcr.Z - 0.5f;
        return new(
            y + 1.402f * cr,
            y - 0.344136f * cb - 0.714136f * cr,
            y + 1.772f * cb);
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Hlsl.Clamp((value - edge0) / Hlsl.Max(edge1 - edge0, 0.0001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float3 Lerp(float3 a, float3 b, float t) => a + (b - a) * t;

    private static float HashNoise(float x, float y, float salt)
    {
        float n = x * 12.9898f + y * 78.233f + salt * 37.719f;
        return Hlsl.Frac(Hlsl.Sin(n) * 43758.5453f);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct NoiseShader : ID2D1PixelShader
{
    private readonly float monoStrength;
    private readonly float colorStrength;

    public NoiseShader(float monoStrength, float colorStrength)
    {
        this.monoStrength = monoStrength;
        this.colorStrength = colorStrength;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float mono = this.monoStrength > 0.0001f
            ? (HashNoise(pos.X, pos.Y, 0f) * 2f - 1f) * this.monoStrength
            : 0f;
        float3 channel = this.colorStrength > 0.0001f
            ? new float3(
                (HashNoise(pos.X, pos.Y, 1f) * 2f - 1f) * this.colorStrength,
                (HashNoise(pos.X, pos.Y, 2f) * 2f - 1f) * this.colorStrength,
                (HashNoise(pos.X, pos.Y, 3f) * 2f - 1f) * this.colorStrength)
            : new float3(0f, 0f, 0f);

        return new(Hlsl.Saturate(color.RGB + mono + channel), color.A);
    }

    private static float HashNoise(float x, float y, float salt)
    {
        float n = x * 12.9898f + y * 78.233f + salt * 37.719f;
        return Hlsl.Frac(Hlsl.Sin(n) * 43758.5453f);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct GammaShader : ID2D1PixelShader
{
    private readonly float invGamma;

    public GammaShader(float invGamma)
    {
        this.invGamma = invGamma;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        return new(Hlsl.Pow(Hlsl.Saturate(color.RGB), this.invGamma), color.A);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct SolidBlockShader : ID2D1PixelShader
{
    private readonly float4 blockColor;
    private readonly float left;
    private readonly float top;
    private readonly float right;
    private readonly float bottom;

    public SolidBlockShader(float4 blockColor, float left, float top, float right, float bottom)
    {
        this.blockColor = blockColor;
        this.left = left;
        this.top = top;
        this.right = right;
        this.bottom = bottom;
    }

    public float4 Execute()
    {
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        return pos.X >= this.left && pos.X < this.right && pos.Y >= this.top && pos.Y < this.bottom
            ? this.blockColor
            : D2D.GetInput(0);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct ScanlineShader : ID2D1PixelShader
{
    private readonly float lineWidth;
    private readonly float spacing;
    private readonly float softness;
    private readonly float angleRadians;
    private readonly float opacity;

    public ScanlineShader(float lineWidth, float spacing, float softness, float angleRadians, float opacity)
    {
        this.lineWidth = lineWidth;
        this.spacing = spacing;
        this.softness = softness;
        this.angleRadians = angleRadians;
        this.opacity = opacity;
    }

    public float4 Execute()
    {
        float4 color = D2D.GetInput(0);
        float4 scenePosition = D2D.GetScenePosition();
        float2 pos = new(scenePosition.X, scenePosition.Y);
        float period = this.lineWidth + this.spacing;
        float cosA = Hlsl.Cos(this.angleRadians);
        float sinA = Hlsl.Sin(this.angleRadians);
        float projected = -pos.X * sinA + pos.Y * cosA;
        float wrapped = projected - Hlsl.Floor(projected / period) * period;
        float sd = wrapped <= this.lineWidth
            ? Hlsl.Min(wrapped, this.lineWidth - wrapped)
            : -Hlsl.Min(wrapped - this.lineWidth, period - wrapped);
        float blur = this.softness * period * 0.5f;
        float darken = blur > 0.01f
            ? this.opacity * Hlsl.Clamp((sd + blur) / (2f * blur), 0f, 1f)
            : (sd >= 0f ? this.opacity : 0f);
        float keep = 1f - darken;

        return new(Hlsl.Saturate(color.RGB * keep), color.A);
    }
}
