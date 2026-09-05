namespace MoveCopyScrap.Services;

/// <summary>Why a file cannot be rotated, so the caller can say something useful.</summary>
public enum RotationSupport
{
    /// <summary>The orientation tag can be rewritten in place.</summary>
    Supported,

    /// <summary>Not an image format that carries an EXIF orientation tag.</summary>
    UnsupportedFormat,

    /// <summary>
    /// A JPEG or TIFF whose metadata block has no orientation tag, and adding one would
    /// mean rewriting the whole EXIF block. See the note on <see cref="RotationService"/>.
    /// </summary>
    NoOrientationTag,

    /// <summary>The file could not be read, or is not the shape its extension claims.</summary>
    Unreadable
}

/// <summary>
/// Rotates pictures by rewriting their EXIF orientation tag - never by re-encoding.
///
/// Why the tag and not the pixels
/// ------------------------------
/// Re-encoding a JPEG loses quality every single time, and a culling pass can easily
/// rotate the same photo twice. Every viewer written this century honours the orientation
/// tag (the eight fixtures under testdata/exif-orientation prove that this app does), so
/// flipping two bytes gives a genuinely lossless rotation that Explorer, Photos, phones
/// and web browsers all agree with. The pixel data is not touched at all: the write is a
/// two-byte seek-and-poke in the middle of the file.
///
/// What is deliberately *not* supported
/// ------------------------------------
/// A JPEG that has an EXIF block but no orientation tag inside it. Adding an entry to
/// IFD0 shifts everything after it by twelve bytes, which invalidates every absolute
/// offset in the block - including the ones buried inside the camera maker's private
/// MakerNote, which no third party can rewrite reliably. Silently corrupting somebody's
/// metadata is far worse than declining, so we decline. Cameras and phones always write
/// the tag, so in practice this only turns up on files that have already been through an
/// editor that stripped it.
///
/// A JPEG with no EXIF block at all is fine: there is nothing to corrupt, so a minimal
/// one is inserted.
///
/// Everything here is synchronous file I/O with no WinRT dependency, so it can also be
/// run during window shutdown, when there is no longer any chance to await.
/// </summary>
public static class RotationService
{
    private static readonly HashSet<string> JpegExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };

    private static readonly HashSet<string> TiffExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".tif", ".tiff" };

    // Composition tables for the eight EXIF orientation values, indexed 1..8.
    // Cw[o] is the orientation that displays the same picture turned a quarter turn
    // clockwise. Verified exhaustively against the reference transforms.
    private static readonly int[] Cw = { 0, 6, 7, 8, 5, 2, 3, 4, 1 };
    private static readonly int[] Ccw = { 0, 8, 5, 6, 7, 4, 1, 2, 3 };

    // An EXIF block lives inside a 64 KB segment near the front of the file; a megabyte
    // of header is far more than enough to walk past any thumbnail or colour profile.
    private const int MaxHeaderBytes = 1 << 20;

    private const string TempSuffix = ".mcs-rotate.tmp";

    /// <summary>Applies <paramref name="quarterTurns"/> clockwise turns to an orientation value.</summary>
    public static int Compose(int orientation, int quarterTurns)
    {
        int o = orientation is >= 1 and <= 8 ? orientation : 1;
        int n = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < n; i++) o = Cw[o];
        return o;
    }

    /// <summary>The inverse of <see cref="Compose"/>, kept beside it so the tables stay in step.</summary>
    public static int ComposeCounter(int orientation, int quarterTurns)
    {
        int o = orientation is >= 1 and <= 8 ? orientation : 1;
        int n = ((quarterTurns % 4) + 4) % 4;
        for (int i = 0; i < n; i++) o = Ccw[o];
        return o;
    }

    /// <summary>Cheap extension test - does not open the file.</summary>
    public static bool IsRotatableFormat(string path)
    {
        string ext = Path.GetExtension(path);
        return JpegExtensions.Contains(ext) || TiffExtensions.Contains(ext);
    }

    /// <summary>Opens the file and reports whether its orientation tag can be rewritten.</summary>
    public static RotationSupport Inspect(string path)
    {
        string ext = Path.GetExtension(path);
        bool jpeg = JpegExtensions.Contains(ext);
        bool tiff = TiffExtensions.Contains(ext);
        if (!jpeg && !tiff) return RotationSupport.UnsupportedFormat;

        try
        {
            byte[] header = ReadHeader(path);
            if (jpeg)
            {
                var scan = ScanJpeg(header);
                if (scan.TagOffset >= 0) return RotationSupport.Supported;
                // No EXIF at all: we can safely add one. EXIF but no tag: we cannot.
                if (scan.CanInsert) return RotationSupport.Supported;
                return scan.Complete ? RotationSupport.NoOrientationTag : RotationSupport.Unreadable;
            }

            return FindOrientationInTiff(header, 0, header.Length, out _, out _, out _)
                ? RotationSupport.Supported
                : RotationSupport.NoOrientationTag;
        }
        catch
        {
            return RotationSupport.Unreadable;
        }
    }

    /// <summary>
    /// Turns the picture <paramref name="quarterTurns"/> quarter turns clockwise (negative
    /// for anticlockwise) by rewriting its orientation tag. Returns false if the file could
    /// not be rewritten; the file is never left half-written.
    /// </summary>
    public static bool Rotate(string path, int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return true;

        string ext = Path.GetExtension(path);
        try
        {
            if (JpegExtensions.Contains(ext)) return RotateJpeg(path, turns);
            if (TiffExtensions.Contains(ext)) return RotateTiff(path, turns);
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Rotate] {path}: {ex.Message}");
            return false;
        }
    }

    public static Task<bool> RotateAsync(string path, int quarterTurns)
        => Task.Run(() => Rotate(path, quarterTurns));

    // ---- JPEG ------------------------------------------------------------

    private static bool RotateJpeg(string path, int turns)
    {
        byte[] header = ReadHeader(path);
        var scan = ScanJpeg(header);

        if (scan.TagOffset >= 0)
        {
            PokeOrientation(path, scan.TagOffset, scan.BigEndian, Compose(scan.Orientation, turns));
            return true;
        }

        // An existing EXIF block we cannot extend, or a header we could not read to the
        // end and so cannot swear has no EXIF in it - see the class comment.
        if (!scan.CanInsert) return false;

        return InsertExifSegment(path, header, Compose(1, turns));
    }

    private readonly struct JpegScan
    {
        public JpegScan(bool hasExif, long tagOffset, bool bigEndian, int orientation, bool complete = true)
        {
            HasExif = hasExif;
            TagOffset = tagOffset;
            BigEndian = bigEndian;
            Orientation = orientation;
            Complete = complete;
        }

        /// <summary>An APP1 segment carrying an "Exif\0\0" block was found.</summary>
        public bool HasExif { get; }

        /// <summary>
        /// The walk reached the image data, so "no EXIF here" really means "no EXIF".
        /// False if it ran off the end of the header buffer or hit something malformed,
        /// in which case an EXIF block might still be sitting further in - and adding a
        /// second one would produce a file with two.
        /// </summary>
        public bool Complete { get; }

        /// <summary>Safe to splice in a new EXIF block: there is definitely not one already.</summary>
        public bool CanInsert => !HasExif && Complete;

        /// <summary>File offset of the orientation value field, or -1.</summary>
        public long TagOffset { get; }

        public bool BigEndian { get; }
        public int Orientation { get; }
    }

    private static JpegScan ScanJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8)
            return new JpegScan(false, -1, false, 1, complete: false);

        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return new JpegScan(false, -1, false, 1, complete: false);

            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }             // fill bytes are legal padding
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is 0xDA or 0xD9) return new JpegScan(false, -1, false, 1);  // image data: done

            int length = (d[i + 2] << 8) | d[i + 3];
            if (length < 2) return new JpegScan(false, -1, false, 1, complete: false);

            int payload = i + 4;
            int payloadLength = length - 2;
            if (payload + payloadLength > d.Length)
                return new JpegScan(false, -1, false, 1, complete: false);

            if (marker == 0xE1 && payloadLength > 8 &&
                d[payload] == (byte)'E' && d[payload + 1] == (byte)'x' &&
                d[payload + 2] == (byte)'i' && d[payload + 3] == (byte)'f' &&
                d[payload + 4] == 0 && d[payload + 5] == 0)
            {
                bool found = FindOrientationInTiff(
                    d, payload + 6, payload + payloadLength, out long offset, out bool be, out int value);
                return new JpegScan(true, found ? offset : -1, be, value);
            }

            i = payload + payloadLength;
        }

        // Ran out of header without reaching the image data.
        return new JpegScan(false, -1, false, 1, complete: false);
    }

    /// <summary>
    /// Walks IFD0 of the TIFF block starting at <paramref name="start"/> looking for tag
    /// 0x0112. Reports the absolute offset of its value field so it can be poked in place.
    /// </summary>
    private static bool FindOrientationInTiff(byte[] d, int start, int limit,
                                              out long valueOffset, out bool bigEndian, out int orientation)
    {
        valueOffset = -1;
        bigEndian = true;
        orientation = 1;

        if (start + 8 > limit) return false;

        if (d[start] == 0x4D && d[start + 1] == 0x4D) bigEndian = true;
        else if (d[start] == 0x49 && d[start + 1] == 0x49) bigEndian = false;
        else return false;

        if (Read16(d, start + 2, bigEndian) != 42) return false;

        long ifd = Read32(d, start + 4, bigEndian);
        if (ifd < 8 || start + ifd + 2 > limit) return false;

        int p = start + (int)ifd;
        int count = Read16(d, p, bigEndian);
        p += 2;

        for (int k = 0; k < count; k++, p += 12)
        {
            if (p + 12 > limit) return false;
            if (Read16(d, p, bigEndian) != 0x0112) continue;

            // Only the standard shape (one SHORT) is patched in place. Anything else -
            // and a LONG orientation does exist in the wild - would need a different
            // number of bytes written, so it is safer to decline than to guess.
            if (Read16(d, p + 2, bigEndian) != 3 || Read32(d, p + 4, bigEndian) != 1) return false;

            int value = Read16(d, p + 8, bigEndian);
            orientation = value is >= 1 and <= 8 ? value : 1;
            valueOffset = p + 8;
            return true;
        }

        return false;
    }

    // ---- TIFF ------------------------------------------------------------

    private static bool RotateTiff(string path, int turns)
    {
        byte[] header = ReadHeader(path);
        if (!FindOrientationInTiff(header, 0, header.Length, out long offset, out bool be, out int current))
            return false;

        PokeOrientation(path, offset, be, Compose(current, turns));
        return true;
    }

    // ---- writing ---------------------------------------------------------

    /// <summary>
    /// The whole point of the exercise: two bytes, in place, with every other byte of the
    /// file - including all of the compressed image data - left exactly as it was.
    /// </summary>
    private static void PokeOrientation(string path, long valueOffset, bool bigEndian, int orientation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(valueOffset, SeekOrigin.Begin);
        stream.WriteByte(bigEndian ? (byte)(orientation >> 8) : (byte)orientation);
        stream.WriteByte(bigEndian ? (byte)orientation : (byte)(orientation >> 8));
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Builds a minimal big-endian EXIF block holding nothing but the orientation tag,
    /// and splices it into a JPEG that had no EXIF of its own.
    /// </summary>
    private static byte[] BuildExifSegment(int orientation)
    {
        // APP1 payload: "Exif\0\0" + TIFF header (8) + entry count (2) + one entry (12)
        //               + next-IFD pointer (4) = 32 bytes. Segment length includes itself.
        var segment = new byte[2 + 2 + 32];
        int i = 0;

        segment[i++] = 0xFF; segment[i++] = 0xE1;
        segment[i++] = 0x00; segment[i++] = 34;              // length = 2 + 32

        segment[i++] = (byte)'E'; segment[i++] = (byte)'x';
        segment[i++] = (byte)'i'; segment[i++] = (byte)'f';
        segment[i++] = 0x00; segment[i++] = 0x00;

        segment[i++] = 0x4D; segment[i++] = 0x4D;            // "MM": big endian
        segment[i++] = 0x00; segment[i++] = 0x2A;            // 42
        segment[i++] = 0x00; segment[i++] = 0x00;            // IFD0 at offset 8
        segment[i++] = 0x00; segment[i++] = 0x08;

        segment[i++] = 0x00; segment[i++] = 0x01;            // one entry
        segment[i++] = 0x01; segment[i++] = 0x12;            // tag 0x0112, Orientation
        segment[i++] = 0x00; segment[i++] = 0x03;            // type SHORT
        segment[i++] = 0x00; segment[i++] = 0x00;            // count 1
        segment[i++] = 0x00; segment[i++] = 0x01;
        segment[i++] = (byte)(orientation >> 8);             // SHORTs sit at the front of the
        segment[i++] = (byte)orientation;                    // four-byte value field
        segment[i++] = 0x00; segment[i++] = 0x00;

        segment[i++] = 0x00; segment[i++] = 0x00;            // no IFD1
        segment[i++] = 0x00; segment[i] = 0x00;

        return segment;
    }

    private static bool InsertExifSegment(string path, byte[] header, int orientation)
    {
        // Slot the new segment in after any leading APP0/JFIF, which is the layout every
        // decoder in the world already copes with.
        int insertAt = 2;
        if (header.Length >= 4 && header[0] == 0xFF && header[1] == 0xD8 &&
            header[2] == 0xFF && header[3] == 0xE0 && header.Length >= 6)
        {
            insertAt = 4 + ((header[4] << 8) | header[5]) - 2 + 2;
            if (insertAt > header.Length) insertAt = 2;
        }

        byte[] segment = BuildExifSegment(orientation);
        string temp = path + TempSuffix;

        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CopyExactly(source, target, insertAt);
                target.Write(segment, 0, segment.Length);
                source.CopyTo(target);
                target.Flush(flushToDisk: true);
            }

            // Replace, not Move: it keeps the original file's attributes and security
            // descriptor, and it is atomic, so a crash can never lose the photo.
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static byte[] ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        int size = (int)Math.Min(MaxHeaderBytes, Math.Max(0, stream.Length));
        var buffer = new byte[size];

        int read = 0;
        while (read < size)
        {
            int n = stream.Read(buffer, read, size - read);
            if (n <= 0) break;
            read += n;
        }

        return read == size ? buffer : buffer[..read];
    }

    private static void CopyExactly(Stream source, Stream target, int count)
    {
        var buffer = new byte[Math.Min(count, 64 * 1024)];
        int left = count;
        while (left > 0)
        {
            int n = source.Read(buffer, 0, Math.Min(buffer.Length, left));
            if (n <= 0) throw new EndOfStreamException("The file ended inside its own header.");
            target.Write(buffer, 0, n);
            left -= n;
        }
    }

    private static int Read16(byte[] d, int at, bool bigEndian)
        => bigEndian ? (d[at] << 8) | d[at + 1] : (d[at + 1] << 8) | d[at];

    private static long Read32(byte[] d, int at, bool bigEndian)
        => bigEndian
            ? ((long)d[at] << 24) | ((long)d[at + 1] << 16) | ((long)d[at + 2] << 8) | d[at + 3]
            : ((long)d[at + 3] << 24) | ((long)d[at + 2] << 16) | ((long)d[at + 1] << 8) | d[at];
}
