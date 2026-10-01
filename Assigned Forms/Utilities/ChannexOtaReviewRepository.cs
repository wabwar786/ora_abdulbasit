using System;
using System.Data;
using System.Data.SqlClient;

namespace hotelsoftware
{
    /// <summary>
    /// Stores Channex OTA reviews in the existing dbo.HotelFeedback table.
    /// Direct feedback and OTA feedback remain separated by ReviewSource.
    /// </summary>
    public sealed class ChannexOtaReviewRepository
    {
        private readonly string _connectionString;

        public ChannexOtaReviewRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException(
                    "A database connection string is required.",
                    "connectionString");

            _connectionString = connectionString;
        }

        public string ResolveHotelIdByPropertyId(string propertyId)
        {
            if (string.IsNullOrWhiteSpace(propertyId))
                return null;

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
       LTRIM(RTRIM(CONVERT(varchar(50), hotel_id)))
FROM dbo.HotelsSignUpTB WITH (READPAST)
WHERE LTRIM(RTRIM(CONVERT(varchar(100), property_id))) = @PropertyId;", connection))
            {
                command.Parameters.Add("@PropertyId", SqlDbType.VarChar, 100).Value =
                    propertyId.Trim();

                connection.Open();
                object value = command.ExecuteScalar();

                return value == null || value == DBNull.Value
                    ? null
                    : Convert.ToString(value).Trim();
            }
        }

        public bool ReviewBelongsToHotel(string hotelId, string channexReviewId)
        {
            if (string.IsNullOrWhiteSpace(hotelId) ||
                string.IsNullOrWhiteSpace(channexReviewId))
            {
                return false;
            }

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.HotelFeedback WITH (READPAST)
WHERE HotelId = @HotelId
  AND ReviewSource = 'OTA'
  AND ChannexReviewId = @ChannexReviewId;", connection))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value =
                    hotelId.Trim();
                command.Parameters.Add("@ChannexReviewId", SqlDbType.VarChar, 50).Value =
                    channexReviewId.Trim();

                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        public void UpdateReply(
            string hotelId,
            string channexReviewId,
            string replyText)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                throw new ArgumentException("Hotel ID is required.", "hotelId");

            if (string.IsNullOrWhiteSpace(channexReviewId))
                throw new ArgumentException("Review ID is required.", "channexReviewId");

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
UPDATE dbo.HotelFeedback
SET
    IsReplied = 1,
    ReplyText = @ReplyText,
    ReviewStatus = 'Replied',
    ExternalUpdatedAtUtc = SYSUTCDATETIME(),
    LastSyncedAtUtc = SYSUTCDATETIME(),
    SyncStatus = 'Synced',
    SyncError = NULL
WHERE HotelId = @HotelId
  AND ReviewSource = 'OTA'
  AND ChannexReviewId = @ChannexReviewId;", connection))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value =
                    hotelId.Trim();
                command.Parameters.Add("@ChannexReviewId", SqlDbType.VarChar, 50).Value =
                    channexReviewId.Trim();
                AddNullableNvarchar(command, "@ReplyText", -1, replyText);

                connection.Open();

                if (command.ExecuteNonQuery() <= 0)
                    throw new InvalidOperationException(
                        "The OTA review could not be found for this hotel.");
            }
        }

        public void Upsert(ChannexOtaReviewModel review)
        {
            ValidateReview(review);

            OtaStayMatch stay = ResolveStay(
                review.HotelId,
                review.ChannexBookingId,
                review.OtaReservationId);

            string regId = stay != null && !string.IsNullOrWhiteSpace(stay.RegId)
                ? stay.RegId
                : "OTA-" + review.ChannexReviewId.Trim();

            string visitId = stay == null ? null : stay.VisitId;
            string guestName = FirstNonEmpty(
                review.GuestName,
                stay == null ? null : stay.GuestName);
            string bookingNumber = FirstNonEmpty(
                review.OtaReservationId,
                stay == null ? null : stay.BookingNumber);

            decimal scale = review.RatingScale <= 0m ? 10m : review.RatingScale;
            int? overallRating = ConvertScoreToFive(review.OverallScore, scale);
            int? cleanlinessRating = ConvertScoreToFive(review.CleanlinessScore, scale);
            int? serviceRating = ConvertScoreToFive(
                review.ServiceScore ?? review.StaffScore,
                scale);
            int? comfortRating = ConvertScoreToFive(review.ComfortScore, scale);
            int? valueRating = ConvertScoreToFive(review.ValueScore, scale);

            string reviewStatus = review.IsHidden
                ? "Hidden"
                : review.IsReplied ? "Replied" : "Published";

            DateTime createdAtUtc = review.ReceivedAtUtc
                ?? review.InsertedAtUtc
                ?? DateTime.UtcNow;

            if (createdAtUtc.Kind != DateTimeKind.Utc)
                createdAtUtc = createdAtUtc.ToUniversalTime();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                using (SqlTransaction transaction =
                       connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    try
                    {
                        int updated;

                        using (SqlCommand command = new SqlCommand(UpdateSql, connection, transaction))
                        {
                            AddReviewParameters(
                                command,
                                review,
                                regId,
                                visitId,
                                guestName,
                                bookingNumber,
                                reviewStatus,
                                scale,
                                overallRating,
                                cleanlinessRating,
                                serviceRating,
                                comfortRating,
                                valueRating,
                                createdAtUtc);

                            updated = command.ExecuteNonQuery();
                        }

                        if (updated == 0)
                        {
                            using (SqlCommand command = new SqlCommand(InsertSql, connection, transaction))
                            {
                                AddReviewParameters(
                                    command,
                                    review,
                                    regId,
                                    visitId,
                                    guestName,
                                    bookingNumber,
                                    reviewStatus,
                                    scale,
                                    overallRating,
                                    cleanlinessRating,
                                    serviceRating,
                                    comfortRating,
                                    valueRating,
                                    createdAtUtc);

                                command.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                    catch (SqlException ex)
                    {
                        transaction.Rollback();

                        // A concurrent webhook/sync may insert the same review first.
                        if (ex.Number == 2601 || ex.Number == 2627)
                        {
                            UpdateExistingAfterDuplicate(
                                review,
                                regId,
                                visitId,
                                guestName,
                                bookingNumber,
                                reviewStatus,
                                scale,
                                overallRating,
                                cleanlinessRating,
                                serviceRating,
                                comfortRating,
                                valueRating,
                                createdAtUtc);
                            return;
                        }

                        throw;
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        private void UpdateExistingAfterDuplicate(
            ChannexOtaReviewModel review,
            string regId,
            string visitId,
            string guestName,
            string bookingNumber,
            string reviewStatus,
            decimal scale,
            int? overallRating,
            int? cleanlinessRating,
            int? serviceRating,
            int? comfortRating,
            int? valueRating,
            DateTime createdAtUtc)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(UpdateSql, connection))
            {
                AddReviewParameters(
                    command,
                    review,
                    regId,
                    visitId,
                    guestName,
                    bookingNumber,
                    reviewStatus,
                    scale,
                    overallRating,
                    cleanlinessRating,
                    serviceRating,
                    comfortRating,
                    valueRating,
                    createdAtUtc);

                connection.Open();
                command.ExecuteNonQuery();
            }
        }

        private OtaStayMatch ResolveStay(
            string hotelId,
            string channexBookingId,
            string otaReservationId)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
    MatchRows.RegId,
    MatchRows.VisitId,
    MatchRows.BookingNumber,
    MatchRows.GuestName
FROM
(
    SELECT
        0 AS SourcePriority,
        LTRIM(RTRIM(CONVERT(varchar(50), nr.reg_id))) AS RegId,
        CAST(NULL AS varchar(50)) AS VisitId,
        COALESCE(
            NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150), nr.BookID))), ''),
            NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150), nr.booking_id))), '')
        ) AS BookingNumber,
        LTRIM(RTRIM(
            ISNULL(CONVERT(nvarchar(200), nr.GuestName), '') +
            CASE
                WHEN ISNULL(CONVERT(nvarchar(200), nr.LastName), '') = '' THEN ''
                ELSE ' ' + CONVERT(nvarchar(200), nr.LastName)
            END
        )) AS GuestName
    FROM dbo.NewReservationsTB nr WITH (READPAST)
    WHERE CONVERT(varchar(50), nr.hotel_id) = @HotelId
      AND
      (
          (ISNULL(@ChannexBookingId, '') <> '' AND
           LTRIM(RTRIM(CONVERT(varchar(100), nr.booking_id))) = @ChannexBookingId)
          OR
          (ISNULL(@OtaReservationId, '') <> '' AND
           LTRIM(RTRIM(CONVERT(nvarchar(150), nr.BookID))) = @OtaReservationId)
      )

    UNION ALL

    SELECT
        1 AS SourcePriority,
        LTRIM(RTRIM(CONVERT(varchar(50), gi.reg_id))) AS RegId,
        NULLIF(LTRIM(RTRIM(CONVERT(varchar(50), gi.visit_id))), '') AS VisitId,
        COALESCE(
            NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150), gi.BookID))), ''),
            NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(150), gi.booking_id))), '')
        ) AS BookingNumber,
        LTRIM(RTRIM(
            ISNULL(CONVERT(nvarchar(200), gi.GuestName), '') +
            CASE
                WHEN ISNULL(CONVERT(nvarchar(200), gi.LastName), '') = '' THEN ''
                ELSE ' ' + CONVERT(nvarchar(200), gi.LastName)
            END
        )) AS GuestName
    FROM dbo.GuestInformationLogTB gi WITH (READPAST)
    WHERE CONVERT(varchar(50), gi.hotel_id) = @HotelId
      AND
      (
          (ISNULL(@ChannexBookingId, '') <> '' AND
           LTRIM(RTRIM(CONVERT(varchar(100), gi.booking_id))) = @ChannexBookingId)
          OR
          (ISNULL(@OtaReservationId, '') <> '' AND
           LTRIM(RTRIM(CONVERT(nvarchar(150), gi.BookID))) = @OtaReservationId)
      )
) MatchRows
ORDER BY MatchRows.SourcePriority;", connection))
            {
                command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value =
                    hotelId.Trim();
                AddNullableVarchar(command, "@ChannexBookingId", 100, channexBookingId);
                AddNullableNvarchar(command, "@OtaReservationId", 150, otaReservationId);

                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        return null;

                    return new OtaStayMatch
                    {
                        RegId = ReadNullableString(reader, "RegId"),
                        VisitId = ReadNullableString(reader, "VisitId"),
                        BookingNumber = ReadNullableString(reader, "BookingNumber"),
                        GuestName = ReadNullableString(reader, "GuestName")
                    };
                }
            }
        }

        private static void ValidateReview(ChannexOtaReviewModel review)
        {
            if (review == null)
                throw new ArgumentNullException("review");

            if (string.IsNullOrWhiteSpace(review.HotelId))
                throw new ArgumentException("Hotel ID is required.", "review.HotelId");

            if (string.IsNullOrWhiteSpace(review.ChannexReviewId))
                throw new ArgumentException("Channex review ID is required.", "review.ChannexReviewId");

            if (string.IsNullOrWhiteSpace(review.ChannexPropertyId))
                throw new ArgumentException("Channex property ID is required.", "review.ChannexPropertyId");
        }

        private static void AddReviewParameters(
            SqlCommand command,
            ChannexOtaReviewModel review,
            string regId,
            string visitId,
            string guestName,
            string bookingNumber,
            string reviewStatus,
            decimal scale,
            int? overallRating,
            int? cleanlinessRating,
            int? serviceRating,
            int? comfortRating,
            int? valueRating,
            DateTime createdAtUtc)
        {
            command.Parameters.Add("@HotelId", SqlDbType.VarChar, 50).Value =
                review.HotelId.Trim();
            command.Parameters.Add("@RegId", SqlDbType.VarChar, 50).Value =
                Truncate(regId, 50);
            AddNullableVarchar(command, "@VisitId", 50, visitId);

            AddNullableTinyInt(command, "@OverallRating", overallRating);
            AddNullableTinyInt(command, "@CleanlinessRating", cleanlinessRating);
            AddNullableTinyInt(command, "@ServiceRating", serviceRating);
            AddNullableTinyInt(command, "@ComfortRating", comfortRating);
            AddNullableTinyInt(command, "@ValueRating", valueRating);

            AddNullableNvarchar(command, "@Comments", -1, review.Content);
            AddNullableNvarchar(command, "@GuestName", 200, guestName);
            AddNullableNvarchar(command, "@BookingNumber", 100, bookingNumber);
            command.Parameters.Add("@ReviewStatus", SqlDbType.VarChar, 30).Value =
                reviewStatus;

            command.Parameters.Add("@ChannexReviewId", SqlDbType.VarChar, 50).Value =
                review.ChannexReviewId.Trim();
            AddNullableVarchar(command, "@ChannexBookingId", 50, review.ChannexBookingId);
            command.Parameters.Add("@ChannexPropertyId", SqlDbType.VarChar, 50).Value =
                review.ChannexPropertyId.Trim();
            AddNullableVarchar(command, "@ChannexChannelId", 50, review.ChannexChannelId);
            AddNullableVarchar(command, "@OtaName", 50, review.OtaName);
            AddNullableNvarchar(command, "@OtaReservationId", 150, review.OtaReservationId);
            AddNullableNvarchar(command, "@OtaReviewId", 150, review.OtaReviewId);

            AddRequiredDecimal(command, "@RatingScale", scale);
            AddNullableDecimal(command, "@OverallScore", review.OverallScore);
            AddNullableDecimal(command, "@CleanlinessScore", review.CleanlinessScore);
            AddNullableDecimal(command, "@ServiceScore", review.ServiceScore);
            AddNullableDecimal(command, "@ComfortScore", review.ComfortScore);
            AddNullableDecimal(command, "@ValueScore", review.ValueScore);
            AddNullableDecimal(command, "@AccuracyScore", review.AccuracyScore);
            AddNullableDecimal(command, "@CheckinScore", review.CheckinScore);
            AddNullableDecimal(command, "@CommunicationScore", review.CommunicationScore);
            AddNullableDecimal(command, "@FacilitiesScore", review.FacilitiesScore);
            AddNullableDecimal(command, "@LocationScore", review.LocationScore);
            AddNullableDecimal(command, "@StaffScore", review.StaffScore);

            command.Parameters.Add("@IsHidden", SqlDbType.Bit).Value = review.IsHidden;
            command.Parameters.Add("@IsReplied", SqlDbType.Bit).Value = review.IsReplied;
            AddNullableNvarchar(command, "@ReplyText", -1, review.ReplyText);
            AddNullableNvarchar(command, "@ScoresJson", -1, review.ScoresJson);
            AddNullableNvarchar(command, "@TagsJson", -1, review.TagsJson);
            AddNullableDateTime2(command, "@ExternalInsertedAtUtc", review.InsertedAtUtc);
            AddNullableDateTime2(command, "@ExternalReceivedAtUtc", review.ReceivedAtUtc);
            AddNullableDateTime2(command, "@ExternalUpdatedAtUtc", review.UpdatedAtUtc);
            AddNullableVarchar(command, "@WebhookEventType", 30, review.WebhookEventType);
            AddNullableNvarchar(command, "@RawPayload", -1, review.RawPayload);
            command.Parameters.Add("@CreatedAtUtc", SqlDbType.DateTime2).Value = createdAtUtc;
        }

        private static int? ConvertScoreToFive(decimal? score, decimal scale)
        {
            if (!score.HasValue || scale <= 0m || score.Value <= 0m)
                return null;

            int value = Convert.ToInt32(Math.Round(
                (score.Value * 5m) / scale,
                0,
                MidpointRounding.AwayFromZero));

            if (value < 1) value = 1;
            if (value > 5) value = 5;
            return value;
        }

        private static void AddRequiredDecimal(
            SqlCommand command,
            string name,
            decimal value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 5;
            parameter.Scale = 2;
            parameter.Value = value;
        }

        private static void AddNullableDecimal(
            SqlCommand command,
            string name,
            decimal? value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 5;
            parameter.Scale = 2;
            parameter.Value = value.HasValue
                ? (object)value.Value
                : DBNull.Value;
        }

        private static void AddNullableTinyInt(
            SqlCommand command,
            string name,
            int? value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.TinyInt);
            parameter.Value = value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static void AddNullableVarchar(
            SqlCommand command,
            string name,
            int length,
            string value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.VarChar, length);
            parameter.Value = string.IsNullOrWhiteSpace(value)
                ? (object)DBNull.Value
                : Truncate(value.Trim(), length);
        }

        private static void AddNullableNvarchar(
            SqlCommand command,
            string name,
            int length,
            string value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.NVarChar, length);
            parameter.Value = string.IsNullOrWhiteSpace(value)
                ? (object)DBNull.Value
                : length < 0 ? value.Trim() : Truncate(value.Trim(), length);
        }

        private static void AddNullableDateTime2(
            SqlCommand command,
            string name,
            DateTime? value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.DateTime2);

            if (!value.HasValue)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            DateTime utc = value.Value.Kind == DateTimeKind.Utc
                ? value.Value
                : value.Value.ToUniversalTime();

            parameter.Value = utc;
        }

        private static string ReadNullableString(SqlDataReader reader, string name)
        {
            return reader[name] == DBNull.Value ? null : Convert.ToString(reader[name]);
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first))
                return first.Trim();

            return string.IsNullOrWhiteSpace(second) ? null : second.Trim();
        }

        private static string Truncate(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || maximumLength < 0)
                return value;

            return value.Length <= maximumLength
                ? value
                : value.Substring(0, maximumLength);
        }

        private sealed class OtaStayMatch
        {
            public string RegId { get; set; }
            public string VisitId { get; set; }
            public string BookingNumber { get; set; }
            public string GuestName { get; set; }
        }

        private const string UpdateSql = @"
UPDATE dbo.HotelFeedback
SET
    HotelId = @HotelId,
    RegId = @RegId,
    VisitId = @VisitId,
    OverallRating = @OverallRating,
    CleanlinessRating = @CleanlinessRating,
    ServiceRating = @ServiceRating,
    ComfortRating = @ComfortRating,
    ValueRating = @ValueRating,
    WouldRecommend = NULL,
    Comments = @Comments,
    PublicReviewConsent = 1,
    GuestName = @GuestName,
    GuestEmail = NULL,
    BookingNumber = @BookingNumber,
    ReviewStatus = @ReviewStatus,

    ReviewSource = 'OTA',
    ExternalProvider = 'Channex',
    ChannexBookingId = @ChannexBookingId,
    ChannexPropertyId = @ChannexPropertyId,
    ChannexChannelId = @ChannexChannelId,
    OtaName = @OtaName,
    OtaReservationId = @OtaReservationId,
    OtaReviewId = @OtaReviewId,
    RatingScale = @RatingScale,
    OverallScore = @OverallScore,
    CleanlinessScore = @CleanlinessScore,
    ServiceScore = @ServiceScore,
    ComfortScore = @ComfortScore,
    ValueScore = @ValueScore,
    AccuracyScore = @AccuracyScore,
    CheckinScore = @CheckinScore,
    CommunicationScore = @CommunicationScore,
    FacilitiesScore = @FacilitiesScore,
    LocationScore = @LocationScore,
    StaffScore = @StaffScore,
    IsHidden = @IsHidden,
    IsReplied = @IsReplied,
    ReplyText = @ReplyText,
    ScoresJson = @ScoresJson,
    TagsJson = @TagsJson,
    ExternalInsertedAtUtc = @ExternalInsertedAtUtc,
    ExternalReceivedAtUtc = @ExternalReceivedAtUtc,
    ExternalUpdatedAtUtc = @ExternalUpdatedAtUtc,
    LastSyncedAtUtc = SYSUTCDATETIME(),
    WebhookEventType = @WebhookEventType,
    RawPayload = @RawPayload,
    SyncStatus = 'Synced',
    SyncError = NULL
WHERE ChannexReviewId = @ChannexReviewId;";

        private const string InsertSql = @"
INSERT INTO dbo.HotelFeedback
(
    InviteId, HotelId, RegId, VisitId,
    OverallRating, CleanlinessRating, ServiceRating, ComfortRating, ValueRating,
    WouldRecommend, Comments, PublicReviewConsent, GuestName, GuestEmail,
    BookingNumber, ReviewStatus, SubmittedIp, UserAgent, CreatedAtUtc,
    ReviewSource, ExternalProvider, ChannexReviewId, ChannexBookingId,
    ChannexPropertyId, ChannexChannelId, OtaName, OtaReservationId, OtaReviewId,
    RatingScale, OverallScore, CleanlinessScore, ServiceScore, ComfortScore,
    ValueScore, AccuracyScore, CheckinScore, CommunicationScore,
    FacilitiesScore, LocationScore, StaffScore, IsHidden, IsReplied, ReplyText,
    ScoresJson, TagsJson, ExternalInsertedAtUtc, ExternalReceivedAtUtc,
    ExternalUpdatedAtUtc, LastSyncedAtUtc, WebhookEventType, RawPayload,
    SyncStatus, SyncError
)
VALUES
(
    NULL, @HotelId, @RegId, @VisitId,
    @OverallRating, @CleanlinessRating, @ServiceRating, @ComfortRating, @ValueRating,
    NULL, @Comments, 1, @GuestName, NULL,
    @BookingNumber, @ReviewStatus, NULL, 'Channex Reviews API', @CreatedAtUtc,
    'OTA', 'Channex', @ChannexReviewId, @ChannexBookingId,
    @ChannexPropertyId, @ChannexChannelId, @OtaName, @OtaReservationId, @OtaReviewId,
    @RatingScale, @OverallScore, @CleanlinessScore, @ServiceScore, @ComfortScore,
    @ValueScore, @AccuracyScore, @CheckinScore, @CommunicationScore,
    @FacilitiesScore, @LocationScore, @StaffScore, @IsHidden, @IsReplied, @ReplyText,
    @ScoresJson, @TagsJson, @ExternalInsertedAtUtc, @ExternalReceivedAtUtc,
    @ExternalUpdatedAtUtc, SYSUTCDATETIME(), @WebhookEventType, @RawPayload,
    'Synced', NULL
);";
    }

    public sealed class ChannexOtaReviewModel
    {
        public string HotelId { get; set; }
        public string ChannexReviewId { get; set; }
        public string ChannexBookingId { get; set; }
        public string ChannexPropertyId { get; set; }
        public string ChannexChannelId { get; set; }
        public string OtaName { get; set; }
        public string OtaReservationId { get; set; }
        public string OtaReviewId { get; set; }
        public string GuestName { get; set; }
        public string Content { get; set; }
        public decimal RatingScale { get; set; }
        public decimal? OverallScore { get; set; }
        public decimal? CleanlinessScore { get; set; }
        public decimal? ServiceScore { get; set; }
        public decimal? ComfortScore { get; set; }
        public decimal? ValueScore { get; set; }
        public decimal? AccuracyScore { get; set; }
        public decimal? CheckinScore { get; set; }
        public decimal? CommunicationScore { get; set; }
        public decimal? FacilitiesScore { get; set; }
        public decimal? LocationScore { get; set; }
        public decimal? StaffScore { get; set; }
        public bool IsHidden { get; set; }
        public bool IsReplied { get; set; }
        public string ReplyText { get; set; }
        public string ScoresJson { get; set; }
        public string TagsJson { get; set; }
        public DateTime? InsertedAtUtc { get; set; }
        public DateTime? ReceivedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string WebhookEventType { get; set; }
        public string RawPayload { get; set; }
    }
}
