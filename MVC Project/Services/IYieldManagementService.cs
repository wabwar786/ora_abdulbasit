using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IYieldManagementService
{
    Task<YieldManagementPageViewModel> GetPageAsync(
        string hotelId,
        string hotelName,
        CancellationToken cancellationToken = default);

    Task<YieldRuleEditorModel?> GetRuleAsync(
        string hotelId,
        int ruleId,
        CancellationToken cancellationToken = default);

    Task<YieldOperationResult> SaveRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        YieldRuleEditorModel request,
        CancellationToken cancellationToken = default, string pcName = "");

    Task<YieldOperationResult> ToggleRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default, string pcName = "");

    Task<YieldOperationResult> DeleteRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default);

    // Reconsider only Yield-owned daily rates affected by a cancelled booking.
    // Restore when no active rule still qualifies for the stay date/plan/category.
    Task<int> RestoreAfterReservationCancellationAsync(
        string hotelId, DateTime arrival, DateTime departure,
        string userId, string userName, string ip,
        CancellationToken cancellationToken = default);

    Task<YieldEvaluationResult> EvaluateActiveRulesAsync(
        string hotelId,
        CancellationToken cancellationToken = default, RateAuditMetadata? originatingAudit = null);
}
