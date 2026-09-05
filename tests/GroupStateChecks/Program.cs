using MoveCopyScrap.Models;
using MoveCopyScrap.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

string directory = Path.Combine(Path.GetTempPath(), "MoveCopyScrap-group-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string folder = Path.Combine(directory, "photos");
    string stateFile;
    using (var store = await MarkStore.OpenAsync(folder, directory)) stateFile = store.StateFilePath;
    await File.WriteAllTextAsync(stateFile, """{"marked":["A.jpg","B.jpg","gone.jpg"]}""");
    string firstId;
    string secondId;
    using (var store = await MarkStore.OpenAsync(folder, directory))
    {
        var groups = store.Groups;
        firstId = groups[0].Id;
        Check(groups.Count == 1 && store.GroupFor("a.JPG") == firstId, "Legacy marks migrate, case-insensitively, into Group 1");
        var second = new MarkGroup { Name = "Archive", ColorIndex = 2, Action = "Move", Destination = Path.Combine(directory, "archive") };
        secondId = second.Id;
        groups.Add(second);
        store.SaveGroups(groups, second.Id);
        store.Assign("A.jpg", second.Id);
        Check(store.Count == 3 && store.GroupFor("A.jpg") == second.Id, "Reassignment keeps one membership per image");
        store.Prune(new[] { "A.jpg", "B.jpg" });
        Check(store.GroupFor("gone.jpg") is null && store.Count == 2, "Pruning removes missing-file assignments");
        store.Flush();
    }
    using (var store = await MarkStore.OpenAsync(folder, directory))
    {
        Check(store.ActiveGroupId == secondId && store.GroupFor("A.jpg") == secondId && store.GroupFor("B.jpg") == firstId,
            "Active group and individual memberships survive reopening");
        var groups = store.Groups;
        Check(groups[1].Name == "Archive" && groups[1].Action == "Move" && groups[1].Destination.EndsWith("archive"),
            "Names, colors and operation plans persist");
        groups[1].Name = "Renamed";
        Check(store.Groups[1].Name == "Archive", "Store snapshots are independent of UI edits");
        store.Assign("A.jpg", null);
        Check(!store.IsMarked("A.jpg") && store.GroupFor("A.jpg") is null, "Unmarking clears membership");
        store.Remove(new[] { "B.jpg" });
        Check(store.Count == 0 && store.GroupFor("B.jpg") is null, "Completed removals clear saved assignments");
        store.Assign("retry.jpg", secondId);
        store.Flush();
    }
    using (var store = await MarkStore.OpenAsync(folder, directory))
    {
        Check(store.Count == 1 && store.GroupFor("retry.jpg") == secondId, "Uncompleted assignments remain available for retry");
        store.ClearAll();
        Check(store.Count == 0 && store.GroupFor("retry.jpg") is null, "ClearAll clears memberships");
    }
    using (var store = await MarkStore.OpenAsync(folder, directory))
        Check(store.Count == 0 && store.Groups.Count == 2, "Empty groups survive reopening");
}
finally
{
    Directory.Delete(directory, recursive: true);
}
