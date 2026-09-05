namespace MoveCopyScrap.Models;

public sealed class MarkGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Group 1";
    public int ColorIndex { get; set; }
    public string Action { get; set; } = "Skip";
    public string Destination { get; set; } = "";

    public MarkGroup Clone() => (MarkGroup)MemberwiseClone();
}
