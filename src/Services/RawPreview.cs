using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MoveCopyScrap.Services;

/// <summary>
/// Finds the JPEG preview that cameras embed in their RAW files, for when Windows has no
/// RAW codec (the free "Raw Image Extension" from the Microsoft Store provides one).
///
/// Almost every RAW format is a TIFF container (Sony ARW, Canon CR2, Nikon NEF, Adobe DNG,
/// Olympus ORF, Panasonic RW2, Pentax PEF, Samsung SRW, ...), with one or more ready-made
/// JPEGs hanging off its directories, so we walk those and keep the biggest JPEG an
/// ordinary decoder can open. Fujifilm RAF has a fixed header that points straight at its
/// JPEG. Anything else (Canon CR3) gets a byte scan of the start of the file.
///
/// Only small, targeted reads are made; a 60 MB RAW is never read whole.
/// </summary>
internal static class RawPreview
{
    /// <summary>A JPEG inside the file, plus the RAW's own orientation tag (1 = upright).</summary>
    public readonly record struct Candidate(long Offset, long Length, int Width, int Height, int Orientation);

    // Byte scan budget for formats we cannot parse.
    private const int ScanLimit = 8 * 1024 * 1024;

    /// <summary>Finds the largest decodable embedded JPEG, or null. Never throws.</summary>
    public static Candidate? Find(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                              bufferSize: 4096, FileOptions.RandomAccess);

            Span<byte> head = stackalloc byte[16];
            if (ReadAt(stream, 0, head) < 16) return null;

            if (head.StartsWith("FUJIFILMCCD-RAW"u8))
                return FromFuji(stream);

            var tiff = FromTiff(stream, head);
            if (tiff is not null) return tiff;

            return FromScan(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the embedded preview as a stream for a <c>BitmapImage</c>, scaled to at most
    /// <paramref name="pixelSize"/> wide (0 = full size) and turned upright. Never throws.
    /// </summary>
    public static async Task<MediaImagePayload?> OpenAsync(string path, uint pixelSize)
    {
        var found = await Task.Run(() => Find(path)).ConfigureAwait(false);
        if (found is not { } candidate) return null;

        InMemoryRandomAccessStream? jpeg = null;
        try
        {
            byte[] bytes = await Task.Run(() =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[candidate.Length];
                file.Seek(candidate.Offset, SeekOrigin.Begin);
                file.ReadExactly(buffer);
                return buffer;
            }).ConfigureAwait(false);

            jpeg = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(jpeg.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            jpeg.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(jpeg);

            if (candidate.Orientation <= 1 || candidate.Orientation > 8)
            {
                // Upright already (or the JPEG carries its own EXIF orientation, as Fuji's
                // do, which BitmapImage applies by itself). Hand the JPEG over as it is.
                uint w = decoder.OrientedPixelWidth;
                uint h = decoder.OrientedPixelHeight;
                jpeg.Seek(0);
                uint decodeWidth = pixelSize == 0 ? w : Math.Min(w, pixelSize);
                var payload = new MediaImagePayload(jpeg, w, h, decodeWidth);
                jpeg = null; // ownership passes to the payload
                return payload;
            }

            // The orientation lives in the RAW, not in the preview, so BitmapImage would show
            // the picture sideways. Decode, scale and turn it here, and pass on an
            // uncompressed BMP (cheap to write, cheap to read back).
            return await TurnAsync(decoder, candidate.Orientation, pixelSize).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RawPreview] {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
        finally
        {
            jpeg?.Dispose();
        }
    }

    private static async Task<MediaImagePayload?> TurnAsync(BitmapDecoder decoder, int orientation, uint pixelSize)
    {
        uint srcW = decoder.PixelWidth;
        uint srcH = decoder.PixelHeight;
        if (srcW == 0 || srcH == 0) return null;

        // TIFF orientation -> BitmapTransform. WIC scales, then flips, then rotates.
        //   2 mirror   3 180   4 flip   5 mirror+270   6 90   7 mirror+90   8 270
        var (rotation, flip) = orientation switch
        {
            2 => (BitmapRotation.None, BitmapFlip.Horizontal),
            3 => (BitmapRotation.Clockwise180Degrees, BitmapFlip.None),
            4 => (BitmapRotation.None, BitmapFlip.Vertical),
            5 => (BitmapRotation.Clockwise270Degrees, BitmapFlip.Horizontal),
            6 => (BitmapRotation.Clockwise90Degrees, BitmapFlip.None),
            7 => (BitmapRotation.Clockwise90Degrees, BitmapFlip.Horizontal),
            8 => (BitmapRotation.Clockwise270Degrees, BitmapFlip.None),
            _ => (BitmapRotation.None, BitmapFlip.None)
        };

        bool quarter = orientation >= 5;
        uint uprightW = quarter ? srcH : srcW;
        uint uprightH = quarter ? srcW : srcH;

        double scale = pixelSize == 0 || uprightW <= pixelSize ? 1.0 : pixelSize / (double)uprightW;
        uint scaledW = Math.Max(1, (uint)Math.Round(srcW * scale));
        uint scaledH = Math.Max(1, (uint)Math.Round(srcH * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = scaledW,
            ScaledHeight = scaledH,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Rotation = rotation,
            Flip = flip
        };

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        var output = new InMemoryRandomAccessStream();
        try
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            output.Seek(0);

            uint outW = quarter ? scaledH : scaledW;
            return new MediaImagePayload(output, uprightW, uprightH, outW);
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    // ---- Fujifilm RAF ---------------------------------------------------------

    private static Candidate? FromFuji(Stream stream)
    {
        // Big-endian JPEG offset and length at bytes 84 and 88 of the header.
        Span<byte> b = stackalloc byte[8];
        if (ReadAt(stream, 84, b) < 8) return null;
        long offset = (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        long length = (uint)((b[4] << 24) | (b[5] << 16) | (b[6] << 8) | b[7]);

        var info = InspectJpeg(stream, offset, length);
        return info is { } j ? new Candidate(offset, length, j.Width, j.Height, 1) : null;
    }

    // ---- TIFF containers --------------------------------------------------------

    private static Candidate? FromTiff(Stream stream, ReadOnlySpan<byte> head)
    {
        bool little;
        if (head[0] == 'I' && head[1] == 'I') little = true;
        else if (head[0] == 'M' && head[1] == 'M') little = false;
        else return null;

        var r = new TiffReader(stream, little);
        ushort magic = r.U16(2);
        // 42 = TIFF (ARW, CR2, NEF, DNG, PEF, SRW, ...); 0x4F52 / 0x5352 = Olympus ORF;
        // 0x55 = Panasonic RW2.
        if (magic != 42 && magic != 0x4F52 && magic != 0x5352 && magic != 0x55) return null;

        long firstIfd = r.U32(4);
        int orientation = 1;
        Candidate? best = null;

        var queue = new Queue<long>();
        var seen = new HashSet<long>();
        queue.Enqueue(firstIfd);
        bool isIfd0 = true;

        while (queue.Count > 0 && seen.Count < 64)
        {
            long ifd = queue.Dequeue();
            if (ifd <= 0 || ifd >= stream.Length || !seen.Add(ifd)) continue;

            int count = r.U16(ifd);
            if (count <= 0 || count > 1000) continue;

            long jpegOffset = 0, jpegLength = 0;
            long stripOffset = 0, stripLength = 0;
            int stripCount = 0;
            int compression = 0;

            for (int i = 0; i < count; i++)
            {
                long entry = ifd + 2 + (i * 12L);
                ushort tag = r.U16(entry);
                ushort type = r.U16(entry + 2);
                uint n = r.U32(entry + 4);

                switch (tag)
                {
                    case 0x0112 when isIfd0: orientation = (int)r.Scalar(entry, type); break;
                    case 0x0103: compression = (int)r.Scalar(entry, type); break;
                    case 0x0201: jpegOffset = r.Scalar(entry, type); break;
                    case 0x0202: jpegLength = r.Scalar(entry, type); break;
                    case 0x0111: stripCount = (int)n; stripOffset = r.Scalar(entry, type); break;
                    case 0x0117: stripLength = r.Scalar(entry, type); break;

                    case 0x014A: // SubIFDs (NEF, DNG, ARW)
                        foreach (long sub in r.Array(entry, type, n, max: 16)) queue.Enqueue(sub);
                        break;

                    case 0x8769: // Exif IFD
                        queue.Enqueue(r.Scalar(entry, type));
                        break;

                    case 0x002E when magic == 0x55: // Panasonic JpgFromRaw: the JPEG is the value
                        Consider(ref best, stream, r.ValueOffset(entry, type, n), n, orientation);
                        break;
                }
            }

            if (jpegOffset > 0 && jpegLength > 0)
                Consider(ref best, stream, jpegOffset, jpegLength, orientation);

            // Old- and new-style JPEG compression in a single strip. The raw image data of
            // CR2 and DNG is also "JPEG" in this sense, but lossless; InspectJpeg rejects it.
            if ((compression == 6 || compression == 7) && stripCount == 1 && stripOffset > 0 && stripLength > 0)
                Consider(ref best, stream, stripOffset, stripLength, orientation);

            long next = r.U32(ifd + 2 + (count * 12L));
            if (next > 0) queue.Enqueue(next);
            isIfd0 = false;
        }

        // The orientation tag may come after the preview's directory was read; it belongs
        // to the file as a whole, so apply the final value.
        return best is { } b ? b with { Orientation = orientation } : null;
    }

    private static void Consider(ref Candidate? best, Stream stream, long offset, long length, int orientation)
    {
        if (offset <= 0 || length < 128 || offset + length > stream.Length) return;
        if (InspectJpeg(stream, offset, length) is not { } info) return;

        long area = (long)info.Width * info.Height;
        if (best is { } b && (long)b.Width * b.Height >= area) return;
        best = new Candidate(offset, length, info.Width, info.Height, orientation);
    }

    // ---- byte scan (CR3 and anything else) -------------------------------------

    private static Candidate? FromScan(Stream stream)
    {
        int size = (int)Math.Min(stream.Length, ScanLimit);
        var data = new byte[size];
        int read = ReadAt(stream, 0, data);

        Candidate? best = null;
        for (int i = 0; i + 4 < read; i++)
        {
            if (data[i] != 0xFF || data[i + 1] != 0xD8 || data[i + 2] != 0xFF) continue;

            long end = FindJpegEnd(data, i, read);
            if (end <= i) continue;

            Consider(ref best, stream, i, end - i, 1);
            if (best is { } b && b.Offset == i) i = (int)end - 1; // skip over what we accepted
        }

        return best;
    }

    /// <summary>Offset just past the EOI marker of the JPEG starting at <paramref name="start"/>, or -1.</summary>
    private static long FindJpegEnd(byte[] data, int start, int limit)
    {
        int p = start + 2;
        // Marker segments up to and including SOS.
        while (p + 4 < limit)
        {
            if (data[p] != 0xFF) return -1;
            byte marker = data[p + 1];
            if (marker == 0xFF) { p++; continue; }
            int len = (data[p + 2] << 8) | data[p + 3];
            if (len < 2) return -1;
            p += 2 + len;
            if (marker == 0xDA) break;
        }

        // Entropy-coded data: the first FF not followed by 00 or a restart marker ends it.
        for (; p + 1 < limit; p++)
        {
            if (data[p] != 0xFF) continue;
            byte next = data[p + 1];
            if (next == 0x00 || (next >= 0xD0 && next <= 0xD7) || next == 0xFF) continue;
            return next == 0xD9 ? p + 2 : -1;
        }

        return -1;
    }

    // ---- JPEG sanity check -------------------------------------------------------

    /// <summary>
    /// Checks that a JPEG starts at <paramref name="offset"/> and is one Windows can decode
    /// (baseline or progressive, not the lossless kind RAW data is stored in), and reads its
    /// size. Returns null otherwise.
    /// </summary>
    private static (int Width, int Height)? InspectJpeg(Stream stream, long offset, long length)
    {
        if (offset <= 0 || length < 128 || offset + length > stream.Length) return null;

        int window = (int)Math.Min(length, 256 * 1024); // the frame header is near the start
        var data = new byte[window];
        int read = ReadAt(stream, offset, data);
        if (read < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;

        int p = 2;
        while (p + 9 < read)
        {
            if (data[p] != 0xFF) return null;
            byte marker = data[p + 1];
            if (marker == 0xFF) { p++; continue; }
            int len = (data[p + 2] << 8) | data[p + 3];
            if (len < 2) return null;

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                int height = (data[p + 5] << 8) | data[p + 6];
                int width = (data[p + 7] << 8) | data[p + 8];
                return width >= 64 && height >= 64 ? (width, height) : null;
            }

            // Any other start-of-frame (lossless, hierarchical, arithmetic) is not for us.
            if (marker >= 0xC3 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return null;

            if (marker == 0xDA) return null; // scan data before a frame header
            p += 2 + len;
        }

        return null;
    }

    private static int ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= stream.Length) return 0;
        stream.Seek(offset, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>Endian-aware reads of TIFF structures at absolute offsets.</summary>
    private sealed class TiffReader
    {
        private readonly Stream _stream;
        private readonly bool _little;
        private readonly byte[] _buffer = new byte[4];

        public TiffReader(Stream stream, bool little)
        {
            _stream = stream;
            _little = little;
        }

        public ushort U16(long offset)
        {
            if (ReadAt(_stream, offset, _buffer.AsSpan(0, 2)) < 2) return 0;
            return _little ? (ushort)(_buffer[0] | (_buffer[1] << 8)) : (ushort)((_buffer[0] << 8) | _buffer[1]);
        }

        public uint U32(long offset)
        {
            if (ReadAt(_stream, offset, _buffer) < 4) return 0;
            return _little
                ? (uint)(_buffer[0] | (_buffer[1] << 8) | (_buffer[2] << 16) | (_buffer[3] << 24))
                : (uint)((_buffer[0] << 24) | (_buffer[1] << 16) | (_buffer[2] << 8) | _buffer[3]);
        }

        private static int TypeSize(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 or 13 => 4,
            _ => 8
        };

        /// <summary>Where the entry's data lives: inline when it fits in four bytes.</summary>
        public long ValueOffset(long entry, ushort type, uint count)
            => (long)TypeSize(type) * count <= 4 ? entry + 8 : U32(entry + 8);

        /// <summary>First value of a SHORT / LONG / IFD entry.</summary>
        public long Scalar(long entry, ushort type)
        {
            uint count = U32(entry + 4);
            long at = ValueOffset(entry, type, Math.Max(1, count));
            return type == 3 ? U16(at) : U32(at);
        }

        public IEnumerable<long> Array(long entry, ushort type, uint count, int max)
        {
            long at = ValueOffset(entry, type, count);
            int size = type == 3 ? 2 : 4;
            for (uint i = 0; i < Math.Min(count, (uint)max); i++)
                yield return size == 2 ? U16(at + (i * 2)) : U32(at + (i * 4));
        }
    }
}
