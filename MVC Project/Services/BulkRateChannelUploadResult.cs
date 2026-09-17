namespace Orapmshms.Services;

public sealed class BulkRateChannelUploadResult
{
    public int PendingRows { get; set; }
    public int UploadedRows { get; set; }
    public int SkippedMappingRows { get; set; }
    public int PayloadGroups { get; set; }
    public List<string> Warnings { get; set;   } = new();
    public bool HasWarnings => Warnings.Count > 0;
}
