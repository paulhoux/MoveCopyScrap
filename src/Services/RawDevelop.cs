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

    /// <summary>
    /// Lateral chromatic aberration: red's and blue's source radius relative to green's,
    /// per knot (1 = drawn at the same size as green), or null.
    /// </summary>
    public float[]? ChromaticRed { get; init; }

    /// <inheritdoc cref="ChromaticRed"/>
    public float[]? ChromaticBlue { get; init; }

    private const ushort TagMake = 0x010F;
    private const ushort TagSubIfds = 0x014A;
    private const ushort TagVignettingParams = 0x7032;
    private const ushort TagChromaticParams = 0x7035;
    private const ushort TagDistortionParams = 0x7037;

    // Units of the chromatic aberration knots, as a fraction of the radius. Measured: on
    // RX10 III shots at 9, 15 and 90 mm, the radial scale that lines red and blue up with
    // green best was found before and after correcting with 2^-21 and with 2^-20. 2^-20
    // took the misregistration from up to 4.4 px (at 3840 px wide) down to about 1 px;
    // 2^-21 only halved it.
    private const float ChromaticUnit = 1f / (1 << 20);

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

            short[]? distortion = null, vignetting = null, chromatic = null;
            foreach (var entry in tiff.Entries(subIfd))
            {
                if (entry.Tag == TagDistortionParams) distortion = tiff.Shorts(entry);
                else if (entry.Tag == TagVignettingParams) vignetting = tiff.Shorts(entry);
                else if (entry.Tag == TagChromaticParams) chromatic = tiff.Shorts(entry);
            }

            float[]? distortionKnots = Knots(distortion);
            if (distortionKnots is null) return null;

            // Chromatic aberration: one list, the red knots followed by the blue ones.
            float[]? chromaticKnots = Knots(chromatic);
            float[]? red = null, blue = null;
            if (chromaticKnots is { Length: >= 4 } && chromaticKnots.Length % 2 == 0)
            {
                int half = chromaticKnots.Length / 2;
                red = chromaticKnots.Take(half).Select(v => 1f + v * ChromaticUnit).ToArray();
                blue = chromaticKnots.Skip(half).Select(v => 1f + v * ChromaticUnit).ToArray();
            }

            // Both encodings as darktable reads them. Distortion: radius multiplier is
            // 1 + v / 2^14. Vignetting: transmission is 2^(0.5 - 2^(v / 2^13 - 1)), which is
            // exactly 1 at v = 0 and falls as v grows towards the corners.
            return new SonyLensCorrection
            {
                Distortion = distortionKnots.Select(v => 1f + v / 16384f).ToArray(),
                Vignetting = Knots(vignetting)?.Select(v => MathF.Pow(2, 0.5f - MathF.Pow(2, v / 8192f - 1))).ToArray(),
                ChromaticRed = red,
                ChromaticBlue = blue
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
/// Lined up like that, the camera's JPEG then also supplies the colours: see
/// <see cref="CameraColourMatch"/>.
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

        var camera = await DecodeCameraJpegAsync(path).ConfigureAwait(false);

        byte[] output = await Task.Run(() =>
        {
            var pixels = Warp(source, srcW, srcH, outW, outH, correction);

            // Give it the camera's colours, learnt from the camera's own JPEG of this
            // shot. Only when that JPEG shows the same frame (same aspect ratio).
            if (camera is { } jpeg &&
                Math.Abs(jpeg.Width / (double)jpeg.Height - fullW / (double)fullH) < 0.01)
            {
                var ours = Warp(source, srcW, srcH, jpeg.Width, jpeg.Height, correction);
                CameraColourMatch.Fit(ours, jpeg.Pixels, jpeg.Width, jpeg.Height)?.ApplyInPlace(pixels, outW, outH);
            }
            return pixels;
        }).ConfigureAwait(false);

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

    /// <summary>The camera's embedded JPEG, upright, as BGRA pixels; null if there is none.</summary>
    private static async Task<(byte[] Pixels, int Width, int Height)?> DecodeCameraJpegAsync(string path)
    {
        try
        {
            var payload = await RawPreview.OpenAsync(path, 0).ConfigureAwait(false);
            if (payload is not { } preview) return null;
            using var stream = preview.Stream;

            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());
            return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resamples the decoded RAW into the camera's frame. The curves are radial: an output
    /// pixel at radius r (0 at the centre, 1 at a corner) takes its colour from radius
    /// r * FrameScale * Distortion(r) in the source, brightened by 1 / Vignetting there.
    /// Red and blue come from slightly different radii again (ChromaticRed / ChromaticBlue),
    /// which lines them up with green and takes out the colour fringes a lens leaves on
    /// high-contrast edges towards the corners.
    /// Radial, so it holds for portrait pictures too.
    /// </summary>
    private static byte[] Warp(byte[] src, int srcW, int srcH, int outW, int outH, SonyLensCorrection correction)
    {
        var dst = new byte[outW * outH * 4];
        float outHalfDiag = MathF.Sqrt(outW * (float)outW + outH * (float)outH) / 2;
        float srcHalfDiag = MathF.Sqrt(srcW * (float)srcW + srcH * (float)srcH) / 2;
        float[] distortion = correction.Distortion;
        float[]? vignetting = correction.Vignetting;
        float[]? red = correction.ChromaticRed, blue = correction.ChromaticBlue;

        Parallel.For(0, outH, y =>
        {
            float uy = (y + 0.5f - outH / 2f) / outHalfDiag;
            int row = y * outW * 4;
            for (int x = 0; x < outW; x++)
            {
                float ux = (x + 0.5f - outW / 2f) / outHalfDiag;
                float r = MathF.Sqrt(ux * ux + uy * uy);
                float m = FrameScale * Interpolate(distortion, r);
                float gain = vignetting is null ? 1f : 1f / Interpolate(vignetting, r * m);

                float mr = red is null ? m : m * Interpolate(red, r);
                float mb = blue is null ? m : m * Interpolate(blue, r);

                // BGRA: channel 0 is blue, 1 green, 2 red.
                int at = row + x * 4;
                dst[at] = Develop(Sample(src, srcW, srcH, ux * mb * srcHalfDiag, uy * mb * srcHalfDiag, 0), gain);
                dst[at + 1] = Develop(Sample(src, srcW, srcH, ux * m * srcHalfDiag, uy * m * srcHalfDiag, 1), gain);
                dst[at + 2] = Develop(Sample(src, srcW, srcH, ux * mr * srcHalfDiag, uy * mr * srcHalfDiag, 2), gain);
                dst[at + 3] = 255;
            }
        });
        return dst;
    }

    /// <summary>One channel, bilinear, at an offset in pixels from the source's centre.</summary>
    private static float Sample(byte[] src, int w, int h, float dx, float dy, int channel)
    {
        float x = Math.Clamp(w / 2f + dx - 0.5f, 0, w - 1);
        float y = Math.Clamp(h / 2f + dy - 0.5f, 0, h - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - x0, fy = y - y0;

        float top = src[(y0 * w + x0) * 4 + channel] + (src[(y0 * w + x1) * 4 + channel] - src[(y0 * w + x0) * 4 + channel]) * fx;
        float bottom = src[(y1 * w + x0) * 4 + channel] + (src[(y1 * w + x1) * 4 + channel] - src[(y1 * w + x0) * 4 + channel]) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Applies the vignetting gain to an sRGB-encoded value.</summary>
    private static byte Develop(float value, float gain)
    {
        if (gain == 1f) return (byte)(value + 0.5f);

        // Light adds up linearly; the decoded pixels are sRGB-encoded.
        int code = (int)value;
        float frac = value - code;
        float linear = code >= 255 ? ToLinear[255] : ToLinear[code] + (ToLinear[code + 1] - ToLinear[code]) * frac;
        int index = (int)(Math.Clamp(linear * gain, 0f, 1f) * (ToSrgbSize - 1) + 0.5f);
        return ToSrgb[index];
    }

    private static float Interpolate(float[] knots, float r)
    {
        float t = Math.Clamp(r, 0f, 1f) * (knots.Length - 1);
        int i = Math.Min((int)t, knots.Length - 2);
        return knots[i] + (knots[i + 1] - knots[i]) * (t - i);
    }
}
