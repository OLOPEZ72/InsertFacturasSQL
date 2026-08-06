namespace InsertFacturasSQL.Models;

public sealed class DocumentReadResult
{
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ExtractedText { get; init; } = "";
    public int? PageCount { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
}
