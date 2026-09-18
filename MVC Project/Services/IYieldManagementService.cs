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
        CancellationToken cancellationToken = default);

    Task<YieldOperationResult> ToggleRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default);

    Task<YieldOperationResult> DeleteRuleAsync(
        string hotelId,
        string userId,
        string userName,
        string ip,
        int ruleId,
        CancellationToken cancellationToken = default);

    Task<YieldEvaluationResult> EvaluateActiveRulesAsync(
        string hotelId,
        CancellationToken cancellationToken = default);
}
