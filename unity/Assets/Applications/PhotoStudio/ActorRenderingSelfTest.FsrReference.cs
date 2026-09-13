using System;
using UnityEngine;

namespace GakumasPhotoMode
{
    public sealed partial class ActorRenderingSelfTest
    {
        // Test-only scalar oracle for the published AMD FSR1 algorithm. Direct
        // clamped taps, not GPU gathers, shader output, or a bilinear substitute.
        // The approximation constants originate in AMD ffx_a.hlsl (MIT);
        // see packages/com.digital-kotone.toolkit/ThirdPartyNotices.md.
        private static Color[] FsrScalar(Color[] source, int width, RectInt viewport,
            Vector2Int output, FsrSettings settings, out Color[] prepared, out Color[] expanded)
        {
            int iw = viewport.width, ih = viewport.height;
            prepared = new Color[iw * ih];
            for (int y = 0; y < ih; y++) for (int x = 0; x < iw; x++)
            {
                var value = source[(y + viewport.y) * width + x + viewport.x];
                for (int c = 0; c < 4; c++) value[c] = float.IsNaN(value[c]) || float.IsInfinity(value[c]) ? 0 : Mathf.Clamp(value[c], 0, c == 3 ? 1 : 65504);
                float maximum = Mathf.Max(value.r, Mathf.Max(value.g, value.b));
                for (int c = 0; c < 3; c++)
                {
                    value[c] = settings.encoding == FsrInputEncoding.LinearHdr ? value[c] / (1 + maximum) : Mathf.Clamp01(value[c]);
                    if (settings.encoding != FsrInputEncoding.PerceptualGamma2Ldr) value[c] = Mathf.Sqrt(value[c]);
                }
                prepared[y * iw + x] = value;
            }
            var input = prepared;
            Color Read(int x, int y) => input[Mathf.Clamp(y, 0, ih - 1) * iw + Mathf.Clamp(x, 0, iw - 1)];
            float Luma(Color c) => c.g + .5f * (c.r + c.b);
            expanded = new Color[output.x * output.y];
            int[] tapX = { 0, 1, -1, 0, 0, -1, 1, 2, 2, 1, 1, 0 };
            int[] tapY = { -1, -1, 1, 1, 0, 0, 1, 1, 0, 0, 2, 2 };
            for (int y = 0; y < output.y; y++) for (int x = 0; x < output.x; x++)
            {
                float px = FsrCoordinate(x, iw, output.x), py = FsrCoordinate(y, ih, output.y);
                int ix = Mathf.FloorToInt(px), iy = Mathf.FloorToInt(py);
                float fx = px - ix, fy = py - iy, dx = 0, dy = 0, edge = 0;
                Color minimum = Read(ix, iy), maximum = minimum;
                for (int cy = 0; cy < 2; cy++) for (int cx = 0; cx < 2; cx++)
                {
                    float weight = (cx == 0 ? 1 - fx : fx) * (cy == 0 ? 1 - fy : fy);
                    int sx = ix + cx, sy = iy + cy;
                    var center = Read(sx, sy);
                    float l = Luma(Read(sx - 1, sy)), r = Luma(Read(sx + 1, sy));
                    float t = Luma(Read(sx, sy - 1)), b = Luma(Read(sx, sy + 1)), m = Luma(center);
                    dx += (r - l) * weight; dy += (b - t) * weight;
                    float ex = Mathf.Clamp01(Mathf.Abs(r - l) * FsrApproxReciprocal(Mathf.Max(Mathf.Abs(r - m), Mathf.Abs(m - l))));
                    float ey = Mathf.Clamp01(Mathf.Abs(b - t) * FsrApproxReciprocal(Mathf.Max(Mathf.Abs(b - m), Mathf.Abs(m - t))));
                    edge += (ex * ex + ey * ey) * weight;
                    for (int c = 0; c < 3; c++) { minimum[c] = Mathf.Min(minimum[c], center[c]); maximum[c] = Mathf.Max(maximum[c], center[c]); }
                }
                float lengthSquared = dx * dx + dy * dy;
                if (lengthSquared < 1f / 32768) dx = 1;
                else
                {
                    float inv = BitConverter.Int32BitsToSingle(unchecked((int)0x5f347d74) - (BitConverter.SingleToInt32Bits(lengthSquared) >> 1));
                    dx *= inv; dy *= inv;
                }
                edge = edge * edge * .25f;
                float stretch = (dx * dx + dy * dy) * FsrApproxReciprocal(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)));
                float along = 1 + (stretch - 1) * edge, across = 1 - .5f * edge;
                float lobe = .5f - .29f * edge, limit = FsrApproxReciprocal(lobe);
                Color sum = Color.clear; float weights = 0;
                for (int tap = 0; tap < tapX.Length; tap++)
                {
                    float ox = tapX[tap] - fx, oy = tapY[tap] - fy;
                    float u = (ox * dx + oy * dy) * along, v = (-ox * dy + oy * dx) * across;
                    float d = Mathf.Min(u * u + v * v, limit), baseWeight = .4f * d - 1, window = lobe * d - 1;
                    float weight = (1.5625f * baseWeight * baseWeight - .5625f) * window * window;
                    sum += Read(ix + tapX[tap], iy + tapY[tap]) * weight; weights += weight;
                }
                for (int c = 0; c < 3; c++) sum[c] = Mathf.Clamp(sum[c] / weights, minimum[c], maximum[c]);
                sum.a = Mathf.Lerp(Mathf.Lerp(Read(ix, iy).a, Read(ix + 1, iy).a, fx),
                    Mathf.Lerp(Read(ix, iy + 1).a, Read(ix + 1, iy + 1).a, fx), fy);
                expanded[y * output.x + x] = sum;
            }
            var easu = expanded;
            Color ReadExpanded(int x, int y) => easu[Mathf.Clamp(y, 0, output.y - 1) * output.x + Mathf.Clamp(x, 0, output.x - 1)];
            var result = new Color[expanded.Length];
            for (int y = 0; y < output.y; y++) for (int x = 0; x < output.x; x++)
            {
                Color value = ReadExpanded(x, y);
                if (settings.sharpen)
                {
                    Color top = ReadExpanded(x, y - 1), left = ReadExpanded(x - 1, y), right = ReadExpanded(x + 1, y), bottom = ReadExpanded(x, y + 1);
                    float lobe = -.1875f;
                    for (int c = 0; c < 3; c++)
                    {
                        float low = Mathf.Min(Mathf.Min(top[c], left[c]), Mathf.Min(right[c], bottom[c]));
                        float high = Mathf.Max(Mathf.Max(top[c], left[c]), Mathf.Max(right[c], bottom[c]));
                        // HLSL max selects the finite operand when the other
                        // limiter is 0/0 at an exactly black/white channel.
                        float minLimiter = high > 0 ? -Mathf.Min(low, value[c]) / (4 * high) : float.NegativeInfinity;
                        float maxLimiter = low < 1 ? (1 - Mathf.Max(high, value[c])) / (4 * low - 4) : float.NegativeInfinity;
                        lobe = Mathf.Max(lobe, Mathf.Min(0, Mathf.Max(minLimiter, maxLimiter)));
                    }
                    lobe *= Mathf.Pow(2, -settings.sharpnessStops);
                    float denominator = 4 * lobe + 1;
                    float initial = BitConverter.Int32BitsToSingle(unchecked((int)0x7ef19fff) - BitConverter.SingleToInt32Bits(denominator));
                    float reciprocal = settings.accurateRcasNormalization ? 1 / denominator : initial * (2 - initial * denominator);
                    for (int c = 0; c < 3; c++) value[c] = (value[c] + lobe * (top[c] + left[c] + bottom[c] + right[c])) * reciprocal;
                }
                for (int c = 0; c < 3; c++)
                {
                    value[c] = Mathf.Clamp01(value[c]);
                    if (settings.encoding != FsrInputEncoding.PerceptualGamma2Ldr) value[c] *= value[c];
                }
                if (settings.encoding == FsrInputEncoding.LinearHdr)
                {
                    float denominator = Mathf.Max(1f / 65505, 1 - Mathf.Max(value.r, Mathf.Max(value.g, value.b)));
                    for (int c = 0; c < 3; c++) value[c] = Mathf.Min(value[c] / denominator, 65504);
                }
                result[y * output.x + x] = value;
            }
            return result;
        }
        private static float FsrApproxReciprocal(float value) =>
            BitConverter.Int32BitsToSingle(unchecked((int)0x7ef07ebb) - BitConverter.SingleToInt32Bits(value));
        private static float FsrFloat(double value) => BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits((float)value));
        private static float FsrCoordinate(int pixel, int input, int output)
        {
            // FsrEasuCon's float32 reciprocal/multiply, then the native D3D11
            // fused multiply-add. At 33->49 row24, an algebraically simplified
            // decimal formula lands on16 while the shader lands just below16.
            // The discrete tap selection must be modeled, not masked or given
            // a relaxed image tolerance. Double evaluates this single FMA
            // before an explicit float32 rounding; the filter remains scalar.
            float scale = FsrFloat(input * (double)FsrFloat(1d / output));
            float offset = FsrFloat(.5d * scale - .5d);
            return FsrFloat(pixel * (double)scale + offset);
        }
    }
}
