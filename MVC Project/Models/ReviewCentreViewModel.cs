namespace Orapmshms.Models;

public sealed class ReviewCentreViewModel
{
    public string HotelName { get; set; } = "Your property";
    public bool CanSyncReviews { get; set; }
    public string? Warning { get; set; }
}

public sealed class ReviewCentrePageResult
{
    public string HotelName { get; set; } = "Your property";
    public long TotalReviews { get; set; }
    public int PageSize { get; set; } = 100;
    public int Offset { get; set; }
    public bool Truncated { get; set; }
    public List<ReviewCentreReview> Reviews { get; set; } = new();
    public ReviewCentreOfficialScores? OfficialScores { get; set; }
    public bool OfficialScoresUnavailable { get; set; }
}

public sealed class ReviewCentreReview
{
    public string? ReviewId { get; set; }
    public string Platform { get; set; } = "Website";
    public string GuestName { get; set; } = "Guest";
    public string Comments { get; set; } = string.Empty;
    public decimal? Score10 { get; set; }
    public bool IsReplied { get; set; }
    public bool IsHidden { get; set; }
    public bool CanReply { get; set; }
    public string ReplyText { get; set; } = string.Empty;
    public string? ReceivedAt { get; set; }
}

public sealed class ReviewReplyRequest
{
    public string ReviewId { get; set; } = string.Empty;
    public string ReplyText { get; set; } = string.Empty;
}

public sealed class ReviewCentreOfficialScores
{
    public decimal? OverallScore10 { get; set; }
    public long? RatingCount { get; set; }
    public Dictionary<string, ReviewCentreOfficialChannel> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReviewCentreOfficialChannel
{
    public decimal? Score10 { get; set; }
    public long? RatingCount { get; set; }
}
