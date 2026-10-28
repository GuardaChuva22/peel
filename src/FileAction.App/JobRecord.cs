namespace FileAction.App;

public sealed record SavedRecipe(string Name, string OperationId, Dictionary<string, string> Parameters);

public sealed class JobRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Started { get; set; } = DateTime.Now;
    public string OperationId { get; set; } = "";
    public string Label { get; set; } = "";
    public string[] Sources { get; set; } = [];
    public string[] Outputs { get; set; } = [];
    public Dictionary<string, string> Parameters { get; set; } = [];
    public string State { get; set; } = "Queued";
    public string? Detail { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public double Progress { get; set; }
}
