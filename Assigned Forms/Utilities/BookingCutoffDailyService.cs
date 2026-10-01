using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace hotelsoftware.Utilities
{
    public sealed class BookingCutoffDailyHotelResult
    {
        public string HotelId { get; set; }
        public bool Success { get; set; }
        public bool HasWarnings { get; set; }
        public int UpdatedRows { get; set; }
        public int InsertedRows { get; set; }
        public int PendingUploadRows { get; set; }
        public int UploadedRows { get; set; }
        public int SkippedMappingRows { get; set; }
        public List<string> Warnings { get; set; } =
            new List<string>();
        public string Error { get; set; }
    }

    public sealed class BookingCutoffDailyRunSummary
    {
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public bool AlreadyRunning { get; set; }
        public int HotelCount { get; set; }
        public int SuccessfulHotels { get; set; }
        public int FailedHotels { get; set; }
        public int WarningHotels { get; set; }
        public int UpdatedRows { get; set; }
        public int InsertedRows { get; set; }
        public int PendingUploadRows { get; set; }
        public int UploadedRows { get; set; }
        public int SkippedMappingRows { get; set; }
        public List<BookingCutoffDailyHotelResult> Hotels { get; set; } =
            new List<BookingCutoffDailyHotelResult>();
    }

    public static class BookingCutoffDailyService
    {
        private const string ApplicationLockName =
            "hotelsoftware.BookingCutoffDailyService";

        public static BookingCutoffDailyRunSummary Run(
            string connectionString,
            int horizonDays = 730)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException(
                    "Connection string is required.",
                    nameof(connectionString));
            }

            if (horizonDays < 1)
                horizonDays = 1;

            if (horizonDays > 1095)
                horizonDays = 1095;

            var summary = new BookingCutoffDailyRunSummary
            {
                StartedUtc = DateTime.UtcNow
            };

            using (var lockConnection =
                   new SqlConnection(connectionString))
            {
                lockConnection.Open();

                int lockResult =
                    AcquireApplicationLock(lockConnection);

                if (lockResult < 0)
                {
                    summary.AlreadyRunning = true;
                    summary.FinishedUtc = DateTime.UtcNow;
                    return summary;
                }

                try
                {
                    List<string> hotelIds =
                        LoadHotelsRequiringCutoffProcessing(
                            lockConnection);

                    summary.HotelCount = hotelIds.Count;

                    foreach (string hotelId in hotelIds)
                    {
                        var hotelResult =
                            new BookingCutoffDailyHotelResult
                            {
                                HotelId = hotelId
                            };

                        try
                        {
                            ChannexUploadWorker
                                .BookingCutoffApplyResult applyResult =
                                ChannexUploadWorker
                                    .RunHotelBookingCutoffSweep(
                                        connectionString,
                                        hotelId,
                                        horizonDays);

                            hotelResult.Success = true;
                            hotelResult.UpdatedRows =
                                applyResult?.UpdatedRows ?? 0;
                            hotelResult.InsertedRows =
                                applyResult?.InsertedRows ?? 0;

                            ChannexUploadWorker.RestrictionUploadResult
                                uploadResult =
                                    applyResult?.Upload;

                            hotelResult.PendingUploadRows =
                                uploadResult?.PendingRows ?? 0;
                            hotelResult.UploadedRows =
                                uploadResult?.UploadedRows ?? 0;
                            hotelResult.SkippedMappingRows =
                                uploadResult?.SkippedMappingRows ?? 0;

                            if (uploadResult?.Warnings != null)
                            {
                                hotelResult.Warnings.AddRange(
                                    uploadResult.Warnings);
                            }

                            hotelResult.HasWarnings =
                                hotelResult.Warnings.Count > 0;

                            summary.SuccessfulHotels++;
                            summary.UpdatedRows +=
                                hotelResult.UpdatedRows;
                            summary.InsertedRows +=
                                hotelResult.InsertedRows;
                            summary.PendingUploadRows +=
                                hotelResult.PendingUploadRows;
                            summary.UploadedRows +=
                                hotelResult.UploadedRows;
                            summary.SkippedMappingRows +=
                                hotelResult.SkippedMappingRows;

                            if (hotelResult.HasWarnings)
                                summary.WarningHotels++;
                        }
                        catch (Exception ex)
                        {
                            hotelResult.Success = false;
                            hotelResult.Error =
                                ex.GetType().Name +
                                ": " +
                                ex.Message;

                            summary.FailedHotels++;
                        }

                        summary.Hotels.Add(hotelResult);
                    }
                }
                finally
                {
                    ReleaseApplicationLock(lockConnection);
                    summary.FinishedUtc = DateTime.UtcNow;
                }
            }

            return summary;
        }

        private static List<string>
            LoadHotelsRequiringCutoffProcessing(
                SqlConnection connection)
        {
            var hotelIds =
                new List<string>();

            using (var cmd = new SqlCommand(@"
SELECT DISTINCT H.hotel_id
FROM
(
    SELECT
        CONVERT(NVARCHAR(50), p.hotel_id) AS hotel_id
    FROM dbo.plans p
    WHERE p.hotel_id IS NOT NULL
      AND p.localplanid IS NOT NULL
      AND ISNULL(p.inactive, 0) = 0
      AND ISNULL(p.booking_cutoff_enabled, 0) = 1
      AND ISNULL(
            TRY_CONVERT(INT, p.booking_cutoff_days),
            0) > 0

    UNION

    SELECT
        CONVERT(NVARCHAR(50), dr.hotel_id) AS hotel_id
    FROM dbo.datesrates dr
    WHERE dr.hotel_id IS NOT NULL
      AND ISNULL(dr.cutoff_stop_sell, 0) = 1
) H
WHERE NULLIF(LTRIM(RTRIM(H.hotel_id)), N'') IS NOT NULL
ORDER BY H.hotel_id;", connection))
            {
                cmd.CommandTimeout = 120;

                using (SqlDataReader reader =
                       cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string hotelId =
                            Convert.ToString(
                                reader["hotel_id"])
                            ?.Trim();

                        if (!string.IsNullOrWhiteSpace(hotelId))
                            hotelIds.Add(hotelId);
                    }
                }
            }

            return hotelIds;
        }

        private static int AcquireApplicationLock(
            SqlConnection connection)
        {
            using (var cmd = new SqlCommand(@"
DECLARE @result INT;

EXEC @result = sys.sp_getapplock
    @Resource = @resource,
    @LockMode = N'Exclusive',
    @LockOwner = N'Session',
    @LockTimeout = 0;

SELECT @result;", connection))
            {
                cmd.Parameters.Add(
                    "@resource",
                    SqlDbType.NVarChar,
                    255).Value = ApplicationLockName;

                object result = cmd.ExecuteScalar();

                return result == null ||
                       result == DBNull.Value
                    ? -999
                    : Convert.ToInt32(result);
            }
        }

        private static void ReleaseApplicationLock(
            SqlConnection connection)
        {
            if (connection == null ||
                connection.State != ConnectionState.Open)
            {
                return;
            }

            using (var cmd = new SqlCommand(@"
EXEC sys.sp_releaseapplock
    @Resource = @resource,
    @LockOwner = N'Session';", connection))
            {
                cmd.Parameters.Add(
                    "@resource",
                    SqlDbType.NVarChar,
                    255).Value = ApplicationLockName;

                cmd.ExecuteNonQuery();
            }
        }
    }
}
