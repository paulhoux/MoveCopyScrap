namespace ImageCuller2.Helpers;

/// <summary>
/// Sorts file names the way Explorer does: "IMG_2.jpg" before "IMG_10.jpg".
/// Pure managed implementation so it behaves identically on every machine.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i], cy = y[j];

            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var sx = x.AsSpan(si, i - si).TrimStart('0');
                var sy = y.AsSpan(sj, j - sj).TrimStart('0');

                if (sx.Length != sy.Length) return sx.Length - sy.Length;
                int numCmp = sx.SequenceCompareTo(sy);
                if (numCmp != 0) return numCmp;
                continue;
            }

            int cmp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
            if (cmp != 0) return cmp;
            i++;
            j++;
        }

        return (x.Length - i) - (y.Length - j);
    }
}
