using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IFrontDeskCalendarService
{
    Task<FrontDeskCalendarPageViewModel> GetPageAsync(
        string hotelId, string hotelName, string userId, string userName, string role,
        DateTime? start, CancellationToken ct = default);

    Task<FrontDeskCalendarPayload> GetCalendarAsync(
        string hotelId, string userId, string userName, string role,
        DateTime start, int days, CancellationToken ct = default);

    Task<FrontDeskBookingDetailsDto?> GetBookingDetailsAsync(
        string hotelId, string regId, int paymentId, CancellationToken ct = default);

    Task<FrontDeskGuestHistoryDto?> GetGuestHistoryAsync(
        string hotelId, string regId, CancellationToken ct = default);

    Task<FrontDeskOperationResult> SaveNoteAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskNoteRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> MarkRoomCleanAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskRoomCleanRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> BlockRoomAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskRoomBlockRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> UpdateBlockAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskRoomBlockUpdateRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> RemoveBlockAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBlockActionRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> ActivateRoomFromTodayAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBlockActionRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> MoveBookingAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBookingMoveRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> ResizeBookingAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskBookingResizeRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> DirectCheckInAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskDirectCheckInRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> PrepareEmailComposerAsync(
        string hotelId, string userId, string baseUrl, FrontDeskEmailPrepareRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> SendEmailAsync(
        string hotelId, string userId, string userName, string ip,
        FrontDeskEmailSendRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> NoShowAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskNoShowRequest request, CancellationToken ct = default);

    Task<FrontDeskOperationResult> CancelReservationAsync(
        string hotelId, string hotelName, string userId, string userName, string ip,
        FrontDeskCancelReservationRequest request, CancellationToken ct = default);
}
