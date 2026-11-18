namespace MoveCopyScrap.Services;

/// <summary>
/// Learns, for one photo, how the camera turned colours into its JPEG, and applies that to
/// the same photo developed from the RAW.
///
/// Windows' RAW codec renders colour its own way: on the RX10 III test shots the result
/// was 7 to 17 dE (CIE76) away from the camera's JPEG - darker, with a much deeper blue
/// sky. Once the RAW has been put into the camera's frame (<see cref="RawDevelop"/>), every
/// pixel of it lines up with the camera's embedded JPEG, so the pair gives thousands of
/// examples of "Windows made this colour, the camera made that one". A 3D colour table
/// fitted to those examples brings the RAW to 1.3 - 2.6 dE of the camera's colours (held-
/// out tiles, four photos), while keeping the RAW's resolution.
///
/// The table only needs to be right for colours this photo contains, which are exactly the
/// colours there are examples of. Table nodes with few or no examples lean on a smooth
/// model (a tone curve per channel plus a colour matrix), so the odd colour the fit never
/// saw still lands somewhere sensible.
///
/// Pixel layout throughout: BGRA, 8 bits per channel.
/// </summary>
internal sealed class CameraColourMatch
{
    private const int Block = 4;            // examples are 4x4 block means: noise and sub-pixel misalignment average out
    private const int Nodes = 33;           // table resolution per channel
    private const double PriorWeight = 2.0; // how many examples' worth of trust the smooth model gets at each node
    private const int MinExamples = 2000;

    private readonly float[] _table;        // Nodes^3 entries of (R, G, B), indexed [r][g][b]

    private CameraColourMatch(float[] table) => _table = table;

    /// <summary>
    /// Fits the camera's colour rendering from <paramref name="ours"/> (the RAW, already in
    /// the camera's frame) and <paramref name="camera"/> (the camera's JPEG), both
    /// <paramref name="width"/> x <paramref name="height"/>. Null when the pair offers too
    /// few usable examples.
    /// </summary>
    public static CameraColourMatch? Fit(byte[] ours, byte[] camera, int width, int height)
    {
        int bw = width / Block, bh = height / Block;
        if (bw < 16 || bh < 16) return null;

        var src = BlockMeans(ours, width, bw, bh);
        var dst = BlockMeans(camera, width, bw, bh);

        // Leave out what would mislead the fit: the frame's margin (where the warp is least
        // exact), edges and busy texture (where a fraction of a pixel of misalignment
        // changes the colour), and anything clipped (no colour information left).
        var busy = BlockBusyness(camera, width, height, bw, bh);
        var sorted = (double[])busy.Clone();
        Array.Sort(sorted);
        double busyCut = sorted[(int)(0.75 * (sorted.Length - 1))];

        var examples = new List<int>();
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                if (x < bw * 0.03 || x > bw * 0.97 || y < bh * 0.03 || y > bh * 0.97) continue;
                int i = y * bw + x;
                if (busy[i] > busyCut) continue;
                if (Max3(src, i) > 250 || Max3(dst, i) > 250) continue;
                examples.Add(i);
            }
        if (examples.Count < MinExamples) return null;

        var curves = FitCurves(src, dst, examples);
        var matrix = FitMatrix(src, dst, examples, curves);
        return new CameraColourMatch(FitTable(src, dst, examples, curves, matrix));
    }

    /// <summary>Maps every pixel of <paramref name="pixels"/> through the table, in place.</summary>
    public void ApplyInPlace(byte[] pixels, int width, int height)
    {
        Parallel.For(0, height, y =>
        {
            int end = (y + 1) * width * 4;
            for (int i = y * width * 4; i < end; i += 4)
            {
                var (r, g, b) = Lookup(pixels[i + 2], pixels[i + 1], pixels[i]);
                pixels[i] = ToByte(b);
                pixels[i + 1] = ToByte(g);
                pixels[i + 2] = ToByte(r);
            }
        });
    }

    private (float R, float G, float B) Lookup(float r, float g, float b)
    {
        const float step = 255f / (Nodes - 1);
        float fr = r / step, fg = g / step, fb = b / step;
        int r0 = Math.Min((int)fr, Nodes - 2), g0 = Math.Min((int)fg, Nodes - 2), b0 = Math.Min((int)fb, Nodes - 2);
        float tr = fr - r0, tg = fg - g0, tb = fb - b0;

        float or = 0, og = 0, ob = 0;
        for (int dr = 0; dr < 2; dr++)
            for (int dg = 0; dg < 2; dg++)
                for (int db = 0; db < 2; db++)
                {
                    float w = (dr == 1 ? tr : 1 - tr) * (dg == 1 ? tg : 1 - tg) * (db == 1 ? tb : 1 - tb);
                    int node = (((r0 + dr) * Nodes + g0 + dg) * Nodes + b0 + db) * 3;
                    or += w * _table[node];
                    og += w * _table[node + 1];
                    ob += w * _table[node + 2];
                }
        return (or, og, ob);
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    // ---- examples ---------------------------------------------------------------------

    /// <summary>Mean (R, G, B) of each block.</summary>
    private static float[] BlockMeans(byte[] px, int width, int bw, int bh)
    {
        var means = new float[bw * bh * 3];
        const float n = Block * Block;
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                float b = 0, g = 0, r = 0;
                for (int yy = 0; yy < Block; yy++)
                {
                    int i = ((y * Block + yy) * width + x * Block) * 4;
                    for (int xx = 0; xx < Block; xx++, i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; }
                }
                int k = (y * bw + x) * 3;
                means[k] = r / n; means[k + 1] = g / n; means[k + 2] = b / n;
            }
        });
        return means;
    }

    /// <summary>Spread of lightness in each block plus a one-pixel margin.</summary>
    private static double[] BlockBusyness(byte[] px, int width, int height, int bw, int bh)
    {
        var spread = new double[bw * bh];
        Parallel.For(0, bh, y =>
        {
            for (int x = 0; x < bw; x++)
            {
                double s = 0, ss = 0; int n = 0;
                for (int yy = -1; yy <= Block; yy++)
                    for (int xx = -1; xx <= Block; xx++)
                    {
                        int px0 = Math.Clamp(x * Block + xx, 0, width - 1), py0 = Math.Clamp(y * Block + yy, 0, height - 1);
                        int i = (py0 * width + px0) * 4;
                        double l = 0.114 * px[i] + 0.587 * px[i + 1] + 0.299 * px[i + 2];
                        s += l; ss += l * l; n++;
                    }
                double mean = s / n;
                spread[y * bw + x] = Math.Sqrt(Math.Max(0, ss / n - mean * mean));
            }
        });
        return spread;
    }

    private static float Max3(float[] rgb, int i) => Math.Max(rgb[i * 3], Math.Max(rgb[i * 3 + 1], rgb[i * 3 + 2]));

    // ---- smooth model: a tone curve per channel, then a colour matrix -----------------

    /// <summary>Per channel, piecewise linear: mean camera value per bin of ours, kept rising.</summary>
    private static (double[] X, double[] Y)[] FitCurves(float[] src, float[] dst, List<int> examples)
    {
        const int bins = 64;
        var curves = new (double[] X, double[] Y)[3];
        for (int c = 0; c < 3; c++)
        {
            var sum = new double[bins];
            var count = new int[bins];
            foreach (int i in examples)
            {
                int bin = Math.Min(bins - 1, (int)(src[i * 3 + c] / 256 * bins));
                sum[bin] += dst[i * 3 + c];
                count[bin]++;
            }

            var xs = new List<double> { 0 };
            var ys = new List<double> { 0 };
            for (int k = 0; k < bins; k++)
            {
                if (count[k] < 5) continue;
                xs.Add((k + 0.5) * 256.0 / bins);
                ys.Add(Math.Max(sum[k] / count[k], ys[^1]));
            }
            xs.Add(255);
            ys.Add(Math.Max(255, ys[^1]));
            curves[c] = (xs.ToArray(), ys.ToArray());
        }
        return curves;
    }

    private static double Curve((double[] X, double[] Y) curve, double v)
    {
        var (xs, ys) = curve;
        int j = Array.BinarySearch(xs, v);
        if (j >= 0) return ys[j];
        j = ~j;
        if (j <= 0) return ys[0];
        if (j >= xs.Length) return ys[^1];
        double t = (v - xs[j - 1]) / (xs[j] - xs[j - 1]);
        return ys[j - 1] + (ys[j] - ys[j - 1]) * t;
    }

    /// <summary>Least-squares 3x3 matrix plus offset, from curve-mapped RGB to camera RGB.</summary>
    private static double[,] FitMatrix(float[] src, float[] dst, List<int> examples, (double[] X, double[] Y)[] curves)
    {
        var ata = new double[4, 4];
        var atb = new double[4, 3];
        var a = new double[4];
        foreach (int i in examples)
        {
            for (int c = 0; c < 3; c++) a[c] = Curve(curves[c], src[i * 3 + c]);
            a[3] = 255;
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++) ata[r, c] += a[r] * a[c];
                for (int c = 0; c < 3; c++) atb[r, c] += a[r] * dst[i * 3 + c];
            }
        }
        for (int r = 0; r < 4; r++) ata[r, r] += 1e-3 * (ata[r, r] + 1);

        var m = new double[3, 4];
        for (int c = 0; c < 3; c++)
        {
            var A = (double[,])ata.Clone();
            var b = new double[4];
            for (int r = 0; r < 4; r++) b[r] = atb[r, c];

            for (int p = 0; p < 4; p++)
            {
                int pivot = p;
                for (int r = p + 1; r < 4; r++) if (Math.Abs(A[r, p]) > Math.Abs(A[pivot, p])) pivot = r;
                for (int k = 0; k < 4; k++) (A[p, k], A[pivot, k]) = (A[pivot, k], A[p, k]);
                (b[p], b[pivot]) = (b[pivot], b[p]);
                for (int r = p + 1; r < 4; r++)
                {
                    double f = A[r, p] / A[p, p];
                    for (int k = p; k < 4; k++) A[r, k] -= f * A[p, k];
                    b[r] -= f * b[p];
                }
            }
            for (int r = 3; r >= 0; r--)
            {
                double s = b[r];
                for (int k = r + 1; k < 4; k++) s -= A[r, k] * m[c, k];
                m[c, r] = s / A[r, r];
            }
        }
        return m;
    }

    // ---- the table --------------------------------------------------------------------

    /// <summary>
    /// Each example is shared out to the 8 nodes around it; each node is the weighted mean
    /// of what the examples say, pulled towards the smooth model in proportion to how few
    /// examples it has.
    /// </summary>
    private static float[] FitTable(float[] src, float[] dst, List<int> examples,
                                    (double[] X, double[] Y)[] curves, double[,] matrix)
    {
        const double step = 255.0 / (Nodes - 1);
        int count = Nodes * Nodes * Nodes;
        var sum = new double[count * 3];
        var weight = new double[count];

        foreach (int i in examples)
        {
            double fr = src[i * 3] / step, fg = src[i * 3 + 1] / step, fb = src[i * 3 + 2] / step;
            int r0 = Math.Min((int)fr, Nodes - 2), g0 = Math.Min((int)fg, Nodes - 2), b0 = Math.Min((int)fb, Nodes - 2);
            double tr = fr - r0, tg = fg - g0, tb = fb - b0;
            for (int dr = 0; dr < 2; dr++)
                for (int dg = 0; dg < 2; dg++)
                    for (int db = 0; db < 2; db++)
                    {
                        double w = (dr == 1 ? tr : 1 - tr) * (dg == 1 ? tg : 1 - tg) * (db == 1 ? tb : 1 - tb);
                        int node = ((r0 + dr) * Nodes + g0 + dg) * Nodes + b0 + db;
                        weight[node] += w;
                        for (int c = 0; c < 3; c++) sum[node * 3 + c] += w * dst[i * 3 + c];
                    }
        }

        var table = new float[count * 3];
        var curved = new double[3];
        for (int r = 0; r < Nodes; r++)
            for (int g = 0; g < Nodes; g++)
                for (int b = 0; b < Nodes; b++)
                {
                    int node = (r * Nodes + g) * Nodes + b;
                    curved[0] = Curve(curves[0], r * step);
                    curved[1] = Curve(curves[1], g * step);
                    curved[2] = Curve(curves[2], b * step);
                    for (int c = 0; c < 3; c++)
                    {
                        double model = matrix[c, 0] * curved[0] + matrix[c, 1] * curved[1] + matrix[c, 2] * curved[2] + matrix[c, 3] * 255;
                        table[node * 3 + c] = (float)((sum[node * 3 + c] + PriorWeight * model) / (weight[node] + PriorWeight));
                    }
                }
        return table;
    }
}
