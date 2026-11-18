using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MoveCopyScrap.Services;

/// <summary>
/// The lens corrections a Sony camera stores in its ARW files: the same curves it applies
/// to its own JPEG. Both are sampled at evenly spaced radii from the centre (0) to the
/// corner (1) of the frame.
/// </summary>
internal sealed class SonyLensCorrection
{
    /// <summary>Source radius multiplier per knot (1 = no change).</summary>
    public required float[] Distortion { get; init; }

    /// <summary>Light reaching the sensor per knot, relative to the centre (1 = no falloff), or null.</summary>
    public float[]? Vignetting { get; init; }

    private const ushort TagMake = 0x010F;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagVignettingParams = 0x7032;
    private const ushort TagDistortionParams = 0x7037;

    /// <summary>
    /// Reads the correction curves from a Sony ARW, or returns null when the file is not a
    /// Sony RAW or carries no distortion data. Only a few small reads; never throws.
    /// </summary>
    public static SonyLensCorrection? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                              bufferSize: 4096, FileOptions.RandomAccess);
            var tiff = Tiff.Open(stream);
            if (tiff is null) return null;

            uint ifd0 = tiff.U32(4);
            string? make = null;
            uint subIfd = 0;
            foreach (var entry in tiff.Entries(ifd0))
            {
                if (entry.Tag == TagMake) make = tiff.Ascii(entry);
                else if (entry.Tag == TagSubIfds) subIfd = tiff.FirstValue(entry);
            }
            if (make is null || !make.StartsWith("SONY", StringComparison.OrdinalIgnoreCase) || subIfd == 0)
                return null;

            short[]? distortion = null, vignetting = null;
            foreach (var entry in tiff.Entries(subIfd))
            {
                if (entry.Tag == TagDistortionParams) distortion = tiff.Shorts(entry);
                else if (entry.Tag == TagVignettingParams) vignetting = tiff.Shorts(entry);
            }

            float[]? distortionKnots = Knots(distortion);
            if (distortionKnots is null) return null;

            // Both encodings as darktable reads them. Distortion: radius multiplier is
            // 1 + v / 2^14. Vignetting: transmission is 2^(0.5 - 2^(v / 2^13 - 1)), which is
            // exactly 1 at v = 0 and falls as v grows towards the corners.
            return new SonyLensCorrection
            {
                Distortion = distortionKnots.Select(v => 1f + v / 16384f).ToArray(),
                Vignetting = Knots(vignetting)?.Select(v => MathF.Pow(2, 0.5f - MathF.Pow(2, v / 8192f - 1))).ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    // The tag holds a knot count followed by the knots (and zero padding).
    private static float[]? Knots(short[]? values)
    {
        if (values is null || values.Length < 3) return null;
        int count = values[0];
        if (count < 2 || count > values.Length - 1) return null;
        return values.Skip(1).Take(count).Select(v => (float)v).ToArray();
    }

    /// <summary>Just enough TIFF to walk two directories.</summary>
    private sealed class Tiff
    {
        public readonly record struct Entry(ushort Tag, ushort Type, uint Count, long ValueAt);

        private readonly Stream _stream;
        private readonly bool _little;

        private Tiff(Stream stream, bool little) { _stream = stream; _little = little; }

        public static Tiff? Open(Stream stream)
        {
            Span<byte> head = stackalloc byte[4];
            stream.Seek(0, SeekOrigin.Begin);
            if (stream.Read(head) < 4) return null;
            if (head.SequenceEqual("II*\0"u8)) return new Tiff(stream, true);
            if (head.SequenceEqual("MM\0*"u8)) return new Tiff(stream, false);
            return null;
        }

        public IEnumerable<Entry> Entries(uint offset)
        {
            if (offset == 0 || offset >= _stream.Length) yield break;
            int count = U16(offset);
            if (count > 1000) yield break;
            for (int i = 0; i < count; i++)
            {
                long at = offset + 2 + 12L * i;
                ushort tag = U16(at), type = U16(at + 2);
                uint n = U32(at + 4);
                long size = (long)n * TypeSize(type);
                yield return new Entry(tag, type, n, size <= 4 ? at + 8 : U32(at + 8));
            }
        }

        public string Ascii(Entry e)
        {
            var bytes = Read(e.ValueAt, (int)Math.Min(e.Count, 64));
            int end = Array.IndexOf(bytes, (byte)0);
            return System.Text.Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end).Trim();
        }

        public uint FirstValue(Entry e) => e.Type == 3 ? U16(e.ValueAt) : U32(e.ValueAt);

        public short[]? Shorts(Entry e)
        {
            if ((e.Type != 3 && e.Type != 8) || e.Count > 256) return null;
            var bytes = Read(e.ValueAt, (int)e.Count * 2);
            var result = new short[e.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = _little ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2))
                                    : BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(i * 2));
            return result;
        }

        public ushort U16(long at)
        {
            var b = Read(at, 2);
            return _little ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
        }

        public uint U32(long at)
        {
            var b = Read(at, 4);
            return _little ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        private byte[] Read(long at, int length)
        {
            var buffer = new byte[length];
            _stream.Seek(at, SeekOrigin.Begin);
            _stream.ReadExactly(buffer);
            return buffer;
        }

        private static int TypeSize(ushort type) => type switch
        {
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 1
        };
    }
}

/// <summary>
/// Shows a RAW at full resolution the way the camera framed it: decoded by Windows' RAW
/// codec (Microsoft's Raw Image Extension), then corrected with the lens data the camera
/// stored in the file.
///
/// Without the correction, a Sony RAW looks quite different from the camera's own JPEG:
/// the lens's barrel distortion bows straight lines, and at the wide end of a zoom the
/// corners of the sensor lie outside the lens's image circle and come out dark. The
/// camera's distortion correction stretches those corners out of the frame, and this
/// does the same. Measured on an RX10 III at 9, 15 and 90 mm, the result lines up with
/// the camera JPEG across the whole frame.
///
/// Only Sony is supported so far; anything else reports <see cref="Outcome.NotPossible"/>
/// and the caller keeps showing the camera's embedded JPEG.
/// </summary>
internal static class RawDevelop
{
    public enum Outcome { Developed, NotPossible, Superseded }

    // Windows decodes a few pixels more than the camera keeps (5496 x 3672 against the
    // RX10 III's 5472 x 3648), and Sony's curves are relative to the camera's frame.
    // Fitted against the camera JPEG; the same at every focal length tested.
    private const float FrameScale = 0.995f;

    // Windows takes about a second to decode a 20 MP RAW, whatever output size is asked
    // for. One decode at a time, and a request that has been overtaken by a newer one
    // while waiting gives up, so stepping through pictures in fill mode only develops the
    // one you stop on.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static long _generation;

    // sRGB <-> linear, for applying the vignetting gain in linear light.
    private static readonly float[] ToLinear = Enumerable.Range(0, 256).Select(i =>
    {
        float c = i / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }).ToArray();

    private const int ToSrgbSize = 4096;
    private static readonly byte[] ToSrgb = Enumerable.Range(0, ToSrgbSize).Select(i =>
    {
        float l = i / (float)(ToSrgbSize - 1);
        float c = l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(c * 255), 0, 255);
    }).ToArray();

    /// <summary>
    /// Develops <paramref name="path"/> to at most <paramref name="width"/> pixels wide.
    /// Never throws.
    /// </summary>
    public static async Task<(Outcome Outcome, MediaImagePayload? Payload)> OpenAsync(string path, uint width)
    {
        var correction = await Task.Run(() => SonyLensCorrection.Read(path)).ConfigureAwait(false);
        if (correction is null) return (Outcome.NotPossible, null);

        long generation = Interlocked.Increment(ref _generation);
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Interlocked.Read(ref _generation)) return (Outcome.Superseded, null);

            var payload = await DevelopAsync(path, width, correction).ConfigureAwait(false);
            return payload is null ? (Outcome.NotPossible, null) : (Outcome.Developed, payload);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RawDevelop] {Path.GetFileName(path)}: {ex.Message}");
            return (Outcome.NotPossible, null);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<MediaImagePayload?> DevelopAsync(string path, uint width, SonyLensCorrection correction)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var input = await file.OpenReadAsync();

        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(input);
        }
        catch
        {
            return null; // no RAW codec installed
        }

        uint fullW = decoder.OrientedPixelWidth, fullH = decoder.OrientedPixelHeight;
        if (fullW == 0 || fullH == 0) return null;

        int outW = (int)Math.Min(width == 0 ? fullW : width, fullW);
        int outH = Math.Max(1, (int)Math.Round(outW * (double)fullH / fullW));

        // Decode somewhat larger than the output, so the warp below samples from a source
        // that is already filtered down (WIC's Fant scaler) rather than aliasing.
        // BitmapTransform sizes are in the stored, unrotated frame.
        double scale = Math.Min(1.0, outW * 1.25 / fullW);
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        byte[] source;
        int srcW, srcH;
        using (var decoded = await decoder.GetSoftwareBitmapAsync(
                   BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                   ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage))
        {
            srcW = decoded.PixelWidth;
            srcH = decoded.PixelHeight;
            source = new byte[srcW * srcH * 4];
            decoded.CopyToBuffer(source.AsBuffer());
        }

        byte[] output = await Task.Run(() => Warp(source, srcW, srcH, outW, outH, correction)).ConfigureAwait(false);

        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                output.AsBuffer(), BitmapPixelFormat.Bgra8, outW, outH, BitmapAlphaMode.Ignore);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            stream.Seek(0);
            return new MediaImagePayload(stream, fullW, fullH, (uint)outW);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resamples the decoded RAW into the camera's frame. The curves are radial: an output
    /// pixel at radius r (0 at the centre, 1 at a corner) takes its colour from radius
    /// r * FrameScale * Distortion(r) in the source, brightened by 1 / Vignetting there.
    /// Radial, so it holds for portrait pictures too.
    /// </summary>
    private static byte[] Warp(byte[] src, int srcW, int srcH, int outW, int outH, SonyLensCorrection correction)
    {
        var dst = new byte[outW * outH * 4];
        float outHalfDiag = MathF.Sqrt(outW * (float)outW + outH * (float)outH) / 2;
        float srcHalfDiag = MathF.Sqrt(srcW * (float)srcW + srcH * (float)srcH) / 2;
        float[] distortion = correction.Distortion;
        float[]? vignetting = correction.Vignetting;

        Parallel.For(0, outH, y =>
        {
            float uy = (y + 0.5f - outH / 2f) / outHalfDiag;
            int row = y * outW * 4;
            for (int x = 0; x < outW; x++)
            {
                float ux = (x + 0.5f - outW / 2f) / outHalfDiag;
                float r = MathF.Sqrt(ux * ux + uy * uy);
                float m = FrameScale * Interpolate(distortion, r);

                float sx = srcW / 2f + ux * m * srcHalfDiag - 0.5f;
                float sy = srcH / 2f + uy * m * srcHalfDiag - 0.5f;
                float gain = vignetting is null ? 1f : 1f / Interpolate(vignetting, r * m);

                SampleBilinear(src, srcW, srcH, sx, sy, gain, dst, row + x * 4);
            }
        });
        return dst;
    }

    private static void SampleBilinear(byte[] src, int w, int h, float x, float y, float gain, byte[] dst, int at)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - x0, fy = y - y0;
        int i00 = (y0 * w + x0) * 4, i10 = (y0 * w + x1) * 4, i01 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;

        for (int c = 0; c < 3; c++)
        {
            float top = src[i00 + c] + (src[i10 + c] - src[i00 + c]) * fx;
            float bottom = src[i01 + c] + (src[i11 + c] - src[i01 + c]) * fx;
            float value = top + (bottom - top) * fy;

            if (gain != 1f)
            {
                // Light adds up linearly; the decoded pixels are sRGB-encoded.
                int code = (int)value;
                float frac = value - code;
                float linear = code >= 255 ? ToLinear[255]
                    : ToLinear[code] + (ToLinear[code + 1] - ToLinear[code]) * frac;
                int index = (int)(Math.Clamp(linear * gain, 0f, 1f) * (ToSrgbSize - 1) + 0.5f);
                dst[at + c] = ToSrgb[index];
            }
            else
            {
                dst[at + c] = (byte)(value + 0.5f);
            }
        }
        dst[at + 3] = 255;
    }

    private static float Interpolate(float[] knots, float r)
    {
        float t = Math.Clamp(r, 0f, 1f) * (knots.Length - 1);
        int i = Math.Min((int)t, knots.Length - 2);
        return knots[i] + (knots[i + 1] - knots[i]) * (t - i);
    }
}
