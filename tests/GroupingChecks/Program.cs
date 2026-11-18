using MoveCopyScrap.Services;

// Checks which files the scanner keeps together (MediaGrouping). Exits non-zero on the
// first failure.

int failures = 0;

void Check(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS: " : "FAIL: ") + message);
    if (!condition) failures++;
}

string Describe(List<MediaGrouping.Group> groups) =>
    string.Join(" | ", groups.Select(g => g.Companions.Count == 0 ? g.Shown : $"{g.Shown} + {string.Join(", ", g.Companions)}"));

void Expect(string message, string[] files, params string[] expected)
{
    var actual = Describe(MediaGrouping.Build(files));
    var wanted = string.Join(" | ", expected);
    Check(actual == wanted, message + (actual == wanted ? "" : $"\n      expected: {wanted}\n      actual:   {actual}"));
}

Expect("RAW+JPEG pair is shown by the JPEG",
    new[] { "DSC1.ARW", "DSC1.JPG" },
    "DSC1.JPG + DSC1.ARW");

Expect("darktable (name.ARW.xmp) and Lightroom (name.xmp) sidecars join the pair",
    new[] { "DSC1.ARW.xmp", "DSC1.xmp", "DSC1.ARW", "DSC1.JPG" },
    "DSC1.JPG + DSC1.ARW, DSC1.ARW.xmp, DSC1.xmp");

Expect("A RAW on its own keeps its sidecar",
    new[] { "DSC2.ARW", "DSC2.ARW.xmp" },
    "DSC2.ARW + DSC2.ARW.xmp");

Expect("Sidecars of non-RAW pictures travel too (HEIC)",
    new[] { "IMG_2987.HEIC", "IMG_2987.HEIC.xmp" },
    "IMG_2987.HEIC + IMG_2987.HEIC.xmp");

Expect("Live Photo: picture and video are separate items; the shared .AAE joins neither",
    new[] { "IMG_0001.MOV", "IMG_0001.HEIC", "IMG_0001.AAE" },
    "IMG_0001.HEIC", "IMG_0001.MOV");

Expect("Live Photo with full-name sidecars: each sidecar joins its own file",
    new[] { "IMG_0002.HEIC", "IMG_0002.MOV", "IMG_0002.HEIC.xmp", "IMG_0002.MOV.xmp" },
    "IMG_0002.HEIC + IMG_0002.HEIC.xmp", "IMG_0002.MOV + IMG_0002.MOV.xmp");

Expect("RAW+JPEG pair beside a video of the same name: pair stays, video is its own item",
    new[] { "DSC4.ARW", "DSC4.JPG", "DSC4.MOV", "DSC4.ARW.xmp", "DSC4.xmp" },
    "DSC4.JPG + DSC4.ARW, DSC4.ARW.xmp", "DSC4.MOV");

Expect("Two RAWs and a JPEG of one name: no clear pair, so three items",
    new[] { "DSC5.ARW", "DSC5.DNG", "DSC5.JPG" },
    "DSC5.ARW", "DSC5.DNG", "DSC5.JPG");

Expect("A picture is never a sidecar of an extension-less file",
    new[] { "photo", "photo.jpg" },
    "photo.jpg + photo");

Expect("Google Takeout metadata (name.jpg.json) joins its picture",
    new[] { "PXL_1.jpg", "PXL_1.jpg.json" },
    "PXL_1.jpg + PXL_1.jpg.json");

Expect("Video with thumbnail and proxy files",
    new[] { "GX010001.MP4", "GX010001.THM", "GX010001.LRV" },
    "GX010001.MP4 + GX010001.LRV, GX010001.THM");

Expect("Only the last extension is the extension: dotted names stay apart",
    new[] { "Trip.2026.jpg", "Trip.2026.xmp", "Trip.2027.jpg" },
    "Trip.2026.jpg + Trip.2026.xmp", "Trip.2027.jpg");

Expect("A Pixel motion photo (name.MP.jpg) is not merged with name.jpg",
    new[] { "PXL_2.MP.jpg", "PXL_2.jpg" },
    "PXL_2.jpg", "PXL_2.MP.jpg");

Expect("Names match regardless of case",
    new[] { "dsc3.arw", "DSC3.JPG", "Dsc3.XMP" },
    "DSC3.JPG + dsc3.arw, Dsc3.XMP");

Expect("Two pictures with one name are separate items",
    new[] { "A.png", "A.jpg" },
    "A.jpg", "A.png");

Expect("Files with no picture or video beside them are not shown",
    new[] { "notes.txt", "B.jpg", "C.xmp" },
    "B.jpg");

Expect("Items come in Explorer's order",
    new[] { "IMG_10.jpg", "IMG_2.jpg", "IMG_1.jpg" },
    "IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg");

Expect("A full-name sidecar joins its file; one whose file is gone is not shown",
    new[] { "X.jpg", "X.jpg.bak", "Y.jpg.bak" },
    "X.jpg + X.jpg.bak");

// The real test folder, when it is there.
string real = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../RAW"));
if (Directory.Exists(real))
{
    var names = Directory.GetFiles(real).Select(Path.GetFileName).OfType<string>().ToList();
    var groups = MediaGrouping.Build(names);
    int grouped = groups.Sum(g => 1 + g.Companions.Count);
    Check(grouped == names.Count, $"RAW folder: all {names.Count} files belong to one of the {groups.Count} items");
    Check(groups.Where(g => g.Shown.EndsWith(".HEIC", StringComparison.OrdinalIgnoreCase))
                .All(g => g.Companions.Any(c => c.EndsWith(".HEIC.xmp", StringComparison.OrdinalIgnoreCase))),
          "RAW folder: every HEIC carries its .HEIC.xmp");
}

Console.WriteLine(failures == 0 ? "All grouping checks passed." : $"{failures} grouping check(s) FAILED.");
return failures == 0 ? 0 : 1;
