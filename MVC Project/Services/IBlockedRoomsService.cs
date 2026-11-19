using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orapmshms.Models;

namespace Orapmshms.Services;

public interface IBlockedRoomsService
{
    Task<bool> HotelIsAllowedAsync(string hotelId, string userId, CancellationToken cancellationToken);

    Task<List<BlockedRoomsOption>> CategoriesAsync(string hotelId, CancellationToken cancellationToken);

    Task<List<string>> RoomsAsync(string hotelId, string categoryId, CancellationToken cancellationToken);

    Task<List<BlockedRoomsRow>> RowsAsync(string hotelId, string? categoryId, CancellationToken cancellationToken);

    Task<BlockedRoomsActionResult> BlockAsync(BlockedRoomsContext who, BlockedRoomsBlockRequest request,
        CancellationToken cancellationToken);

    Task<BlockedRoomsActionResult> BulkBlockAsync(BlockedRoomsContext who, BlockedRoomsBulkBlockRequest request,
        CancellationToken cancellationToken);

    Task<BlockedRoomsActionResult> UpdateAsync(BlockedRoomsContext who, BlockedRoomsUpdateRequest request,
        CancellationToken cancellationToken);

    Task LogErrorAsync(BlockedRoomsContext who, string description, CancellationToken cancellationToken);
}
