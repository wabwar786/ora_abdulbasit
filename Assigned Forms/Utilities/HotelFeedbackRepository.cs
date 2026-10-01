using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;

namespace hotelsoftware
{
    public sealed class HotelFeedbackRepository
    {
        private readonly string _connectionString;

        public HotelFeedbackRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A database connection string is required.", "connectionString");

            _connectionString = connectionString;
        }

        public string CreateInvite(
            string hotelId,
            string regId,
            string visitId,
            string hotelName,
            string hotelLogoUrl,
            string guestName,
            string guestEmail,
            string bookingNumber,
            DateTime expiresAtUtc)
        {
            ValidateRequiredLength(hotelId, 50, "hotelId");
            ValidateRequiredLength(regId, 50, "regId");
            ValidateRequiredLength(hotelName, 200, "hotelName");

            visitId = NormalizeOptional(visitId, 50);
            hotelLogoUrl = NormalizeOptional(hotelLogoUrl, 1000);
            guestName = NormalizeOptional(guestName, 200);
            guestEmail = NormalizeOptional(guestEmail, 254);
            bookingNumber = NormalizeOptional(bookingNumber, 100);

            if (expiresAtUtc.Kind != DateTimeKind.Utc)
                expiresAtUtc = expiresAtUtc.ToUniversalTime();

            if (expiresAtUtc <= DateTime.UtcNow)
                throw new ArgumentOutOfRangeException("expiresAtUtc", "The feedback invite expiry must be in the future.");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                string rawToken = GenerateToken();
                string tokenHash = ComputeTokenHash(rawToken);

                try
                {
                    using (SqlConnection connection = new SqlConnection(_connectionString))
                    using (SqlCommand command = new SqlCommand(@"
INSERT INTO dbo.HotelFeedbackInvite
(
    TokenHash,
    HotelId,
    RegId,
    VisitId,
    HotelName,
    HotelLogoUrl,
    GuestName,
    GuestEmail,
    BookingNumber,
    ExpiresAtUtc,
    IsActive,
    CreatedAtUtc
)
VALUES
(
    @TokenHash,
    @HotelId,
    @RegId,
    @VisitId,
    @HotelName,
    @HotelLogoUrl,
    @GuestName,
    @GuestEmail,
    @BookingNumber,
    @ExpiresAtUtc,
    1,
    SYSUTCDATETIME()
);", connection))
                    {
                        AddRequiredVarchar(command, "@TokenHash", 64, tokenHash);
                        AddRequiredVarchar(command, "@HotelId", 50, hotelId.Trim());
                        AddRequiredVarchar(command, "@RegId", 50, regId.Trim());
                        AddNullableVarchar(command, "@VisitId", 50, visitId);
                        AddRequiredNvarchar(command, "@HotelName", 200, hotelName.Trim());
                        AddNullableNvarchar(command, "@HotelLogoUrl", 1000, hotelLogoUrl);
                        AddNullableNvarchar(command, "@GuestName", 200, guestName);
                        AddNullableNvarchar(command, "@GuestEmail", 254, guestEmail);
                        AddNullableNvarchar(command, "@BookingNumber", 100, bookingNumber);
                        command.Parameters.Add("@ExpiresAtUtc", SqlDbType.DateTime2).Value = expiresAtUtc;

                        connection.Open();
                        command.ExecuteNonQuery();
                    }

                    return rawToken;
                }
                catch (SqlException ex)
                {
                    if ((ex.Number == 2601 || ex.Number == 2627) && attempt < 2)
                        continue;

                    throw;
                }
            }

            throw new InvalidOperationException("Unable to create a unique feedback invite.");
        }

        public HotelFeedbackInviteModel GetInviteByToken(string rawToken)
        {
            if (!IsValidRawToken(rawToken))
                return null;

            string tokenHash = ComputeTokenHash(rawToken);

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
    InviteId,
    HotelId,
    RegId,
    VisitId,
    HotelName,
    HotelLogoUrl,
    GuestName,
    GuestEmail,
    BookingNumber,
    ExpiresAtUtc,
    UsedAtUtc,
    IsActive
FROM dbo.HotelFeedbackInvite
WHERE TokenHash = @TokenHash;", connection))
            {
                AddRequiredVarchar(command, "@TokenHash", 64, tokenHash);

                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        return null;

                    return new HotelFeedbackInviteModel
                    {
                        InviteId = Convert.ToInt64(reader["InviteId"]),
                        HotelId = Convert.ToString(reader["HotelId"]),
                        RegId = Convert.ToString(reader["RegId"]),
                        VisitId = ReadNullableString(reader, "VisitId"),
                        HotelName = Convert.ToString(reader["HotelName"]),
                        HotelLogoUrl = ReadNullableString(reader, "HotelLogoUrl"),
                        GuestName = ReadNullableString(reader, "GuestName"),
                        GuestEmail = ReadNullableString(reader, "GuestEmail"),
                        BookingNumber = ReadNullableString(reader, "BookingNumber"),
                        ExpiresAtUtc = DateTime.SpecifyKind(
                            Convert.ToDateTime(reader["ExpiresAtUtc"]),
                            DateTimeKind.Utc),
                        UsedAtUtc = reader["UsedAtUtc"] == DBNull.Value
                            ? (DateTime?)null
                            : DateTime.SpecifyKind(
                                Convert.ToDateTime(reader["UsedAtUtc"]),
                                DateTimeKind.Utc),
                        IsActive = Convert.ToBoolean(reader["IsActive"])
                    };
                }
            }
        }

        public SaveHotelFeedbackResult SaveFeedback(
            string rawToken,
            HotelFeedbackSubmissionModel submission)
        {
            if (!IsValidRawToken(rawToken) || submission == null)
                return SaveHotelFeedbackResult.InvalidInvite;

            ValidateSubmission(submission);

            string tokenHash = ComputeTokenHash(rawToken);

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                using (SqlTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                {
                    try
                    {
                        HotelFeedbackInviteModel invite;

                        using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
    InviteId,
    HotelId,
    RegId,
    VisitId,
    HotelName,
    HotelLogoUrl,
    GuestName,
    GuestEmail,
    BookingNumber,
    ExpiresAtUtc,
    UsedAtUtc,
    IsActive
FROM dbo.HotelFeedbackInvite WITH (UPDLOCK, HOLDLOCK)
WHERE TokenHash = @TokenHash;", connection, transaction))
                        {
                            AddRequiredVarchar(command, "@TokenHash", 64, tokenHash);

                            using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                            {
                                if (!reader.Read())
                                {
                                    transaction.Rollback();
                                    return SaveHotelFeedbackResult.InvalidInvite;
                                }

                                invite = new HotelFeedbackInviteModel
                                {
                                    InviteId = Convert.ToInt64(reader["InviteId"]),
                                    HotelId = Convert.ToString(reader["HotelId"]),
                                    RegId = Convert.ToString(reader["RegId"]),
                                    VisitId = ReadNullableString(reader, "VisitId"),
                                    HotelName = Convert.ToString(reader["HotelName"]),
                                    HotelLogoUrl = ReadNullableString(reader, "HotelLogoUrl"),
                                    GuestName = ReadNullableString(reader, "GuestName"),
                                    GuestEmail = ReadNullableString(reader, "GuestEmail"),
                                    BookingNumber = ReadNullableString(reader, "BookingNumber"),
                                    ExpiresAtUtc = DateTime.SpecifyKind(
                                        Convert.ToDateTime(reader["ExpiresAtUtc"]),
                                        DateTimeKind.Utc),
                                    UsedAtUtc = reader["UsedAtUtc"] == DBNull.Value
                                        ? (DateTime?)null
                                        : DateTime.SpecifyKind(
                                            Convert.ToDateTime(reader["UsedAtUtc"]),
                                            DateTimeKind.Utc),
                                    IsActive = Convert.ToBoolean(reader["IsActive"])
                                };
                            }
                        }

                        if (!invite.IsActive)
                        {
                            transaction.Rollback();
                            return SaveHotelFeedbackResult.InvalidInvite;
                        }

                        if (invite.UsedAtUtc.HasValue)
                        {
                            transaction.Rollback();
                            return SaveHotelFeedbackResult.AlreadySubmitted;
                        }

                        if (invite.ExpiresAtUtc <= DateTime.UtcNow)
                        {
                            transaction.Rollback();
                            return SaveHotelFeedbackResult.ExpiredInvite;
                        }

                        using (SqlCommand command = new SqlCommand(@"
INSERT INTO dbo.HotelFeedback
(
    InviteId,
    HotelId,
    RegId,
    VisitId,
    OverallRating,
    CleanlinessRating,
    ServiceRating,
    ComfortRating,
    ValueRating,
    WouldRecommend,
    Comments,
    PublicReviewConsent,
    GuestName,
    GuestEmail,
    BookingNumber,
    ReviewStatus,
    SubmittedIp,
    UserAgent,
    CreatedAtUtc,

    ReviewSource,
    ExternalProvider,
    RatingScale,
    OverallScore,
    CleanlinessScore,
    ServiceScore,
    ComfortScore,
    ValueScore,
    IsHidden,
    IsReplied,
    SyncStatus,
    LastSyncedAtUtc
)
VALUES
(
    @InviteId,
    @HotelId,
    @RegId,
    @VisitId,
    @OverallRating,
    @CleanlinessRating,
    @ServiceRating,
    @ComfortRating,
    @ValueRating,
    @WouldRecommend,
    @Comments,
    @PublicReviewConsent,
    @GuestName,
    @GuestEmail,
    @BookingNumber,
    'Pending',
    @SubmittedIp,
    @UserAgent,
    SYSUTCDATETIME(),

    'Direct',
    NULL,
    5.00,
    CONVERT(decimal(5,2), @OverallRating),
    CONVERT(decimal(5,2), @CleanlinessRating),
    CONVERT(decimal(5,2), @ServiceRating),
    CONVERT(decimal(5,2), @ComfortRating),
    CONVERT(decimal(5,2), @ValueRating),
    0,
    0,
    'Synced',
    SYSUTCDATETIME()
);

UPDATE dbo.HotelFeedbackInvite
SET UsedAtUtc = SYSUTCDATETIME()
WHERE InviteId = @InviteId
  AND UsedAtUtc IS NULL;", connection, transaction))
                        {
                            command.Parameters.Add("@InviteId", SqlDbType.BigInt).Value = invite.InviteId;
                            AddRequiredVarchar(command, "@HotelId", 50, invite.HotelId);
                            AddRequiredVarchar(command, "@RegId", 50, invite.RegId);
                            AddNullableVarchar(command, "@VisitId", 50, invite.VisitId);

                            command.Parameters.Add("@OverallRating", SqlDbType.TinyInt).Value =
                                submission.OverallRating;

                            AddNullableTinyInt(
                                command,
                                "@CleanlinessRating",
                                submission.CleanlinessRating);

                            AddNullableTinyInt(
                                command,
                                "@ServiceRating",
                                submission.ServiceRating);

                            AddNullableTinyInt(
                                command,
                                "@ComfortRating",
                                submission.ComfortRating);

                            AddNullableTinyInt(
                                command,
                                "@ValueRating",
                                submission.ValueRating);

                            SqlParameter recommendParameter =
                                command.Parameters.Add("@WouldRecommend", SqlDbType.Bit);

                            recommendParameter.Value = submission.WouldRecommend.HasValue
                                ? (object)submission.WouldRecommend.Value
                                : DBNull.Value;

                            AddNullableNvarchar(
                                command,
                                "@Comments",
                                2000,
                                NormalizeOptional(submission.Comments, 2000));

                            command.Parameters.Add(
                                "@PublicReviewConsent",
                                SqlDbType.Bit).Value = submission.PublicReviewConsent;

                            AddNullableNvarchar(command, "@GuestName", 200, invite.GuestName);
                            AddNullableNvarchar(command, "@GuestEmail", 254, invite.GuestEmail);
                            AddNullableNvarchar(command, "@BookingNumber", 100, invite.BookingNumber);
                            AddNullableVarchar(
                                command,
                                "@SubmittedIp",
                                45,
                                NormalizeOptional(submission.SubmittedIp, 45));
                            AddNullableNvarchar(
                                command,
                                "@UserAgent",
                                500,
                                NormalizeOptional(submission.UserAgent, 500));

                            command.ExecuteNonQuery();
                        }

                        transaction.Commit();
                        return SaveHotelFeedbackResult.Submitted;
                    }
                    catch (SqlException ex)
                    {
                        transaction.Rollback();

                        if (ex.Number == 2601 || ex.Number == 2627)
                            return SaveHotelFeedbackResult.AlreadySubmitted;

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

        // Returns all reservation IDs that have a visible Direct or OTA review.
        // One query is used for the complete calendar to avoid per-cell calls.
        public IList<string> GetReviewedRegIds(string hotelId)
        {
            ValidateRequiredLength(hotelId, 50, "hotelId");

            List<string> result = new List<string>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT DISTINCT
       LTRIM(RTRIM(CONVERT(varchar(50), RegId))) AS RegId
FROM dbo.HotelFeedback WITH (READPAST)
WHERE HotelId = @HotelId
  AND RegId IS NOT NULL
  AND LTRIM(RTRIM(CONVERT(varchar(50), RegId))) <> ''
  AND
  (
      ISNULL(ReviewSource, 'Direct') <> 'OTA'
      OR ISNULL(IsHidden, 0) = 0
  );", connection))
            {
                AddRequiredVarchar(command, "@HotelId", 50, hotelId.Trim());
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string regId = ReadNullableString(reader, "RegId");
                        if (!string.IsNullOrWhiteSpace(regId))
                            result.Add(regId.Trim());
                    }
                }
            }

            return result;
        }

        // Returns the latest visible Direct or OTA review for one stay.
        public HotelFeedbackReviewModel GetReviewByStay(string hotelId, string regId)
        {
            ValidateRequiredLength(hotelId, 50, "hotelId");
            ValidateRequiredLength(regId, 50, "regId");

            using (SqlConnection connection = new SqlConnection(_connectionString))
            using (SqlCommand command = new SqlCommand(@"
SELECT TOP (1)
    FeedbackId,
    HotelId,
    RegId,
    ISNULL(ReviewSource, 'Direct') AS ReviewSource,
    ExternalProvider,
    ChannexReviewId,
    ChannexBookingId,
    OtaName,
    OtaReservationId,

    OverallRating,
    CleanlinessRating,
    ServiceRating,
    ComfortRating,
    ValueRating,
    WouldRecommend,

    COALESCE(OverallScore, CONVERT(decimal(5,2), OverallRating)) AS OverallScore,
    COALESCE(
        RatingScale,
        CASE WHEN ISNULL(ReviewSource, 'Direct') = 'OTA' THEN 10.00 ELSE 5.00 END
    ) AS RatingScale,
    COALESCE(CleanlinessScore, CONVERT(decimal(5,2), CleanlinessRating)) AS CleanlinessScore,
    COALESCE(ServiceScore, StaffScore, CONVERT(decimal(5,2), ServiceRating)) AS ServiceScore,
    COALESCE(ComfortScore, CONVERT(decimal(5,2), ComfortRating)) AS ComfortScore,
    COALESCE(ValueScore, CONVERT(decimal(5,2), ValueRating)) AS ValueScore,
    AccuracyScore,
    CheckinScore,
    CommunicationScore,
    FacilitiesScore,
    LocationScore,
    StaffScore,

    ISNULL(IsHidden, 0) AS IsHidden,
    ISNULL(IsReplied, 0) AS IsReplied,
    ReplyText,
    Comments,
    COALESCE(ExternalReceivedAtUtc, ExternalInsertedAtUtc, CreatedAtUtc) AS DisplayCreatedAtUtc
FROM dbo.HotelFeedback WITH (READPAST)
WHERE HotelId = @HotelId
  AND RegId = @RegId
  AND
  (
      ISNULL(ReviewSource, 'Direct') <> 'OTA'
      OR ISNULL(IsHidden, 0) = 0
  )
ORDER BY
    COALESCE(ExternalReceivedAtUtc, ExternalInsertedAtUtc, CreatedAtUtc) DESC,
    FeedbackId DESC;", connection))
            {
                AddRequiredVarchar(command, "@HotelId", 50, hotelId.Trim());
                AddRequiredVarchar(command, "@RegId", 50, regId.Trim());
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (!reader.Read())
                        return null;

                    decimal ratingScale = ReadNullableDecimal(reader, "RatingScale") ?? 5m;
                    decimal overallScore = ReadNullableDecimal(reader, "OverallScore") ?? 0m;
                    int overallRating = ReadNullableInt(reader, "OverallRating")
                        ?? ConvertScoreToFive(overallScore, ratingScale)
                        ?? 0;

                    return new HotelFeedbackReviewModel
                    {
                        FeedbackId = Convert.ToInt64(reader["FeedbackId"]),
                        HotelId = Convert.ToString(reader["HotelId"]),
                        RegId = Convert.ToString(reader["RegId"]),
                        ReviewSource = Convert.ToString(reader["ReviewSource"]),
                        ExternalProvider = ReadNullableString(reader, "ExternalProvider"),
                        ChannexReviewId = ReadNullableString(reader, "ChannexReviewId"),
                        ChannexBookingId = ReadNullableString(reader, "ChannexBookingId"),
                        OtaName = ReadNullableString(reader, "OtaName"),
                        OtaReservationId = ReadNullableString(reader, "OtaReservationId"),

                        OverallRating = overallRating,
                        CleanlinessRating = ReadNullableInt(reader, "CleanlinessRating"),
                        ServiceRating = ReadNullableInt(reader, "ServiceRating"),
                        ComfortRating = ReadNullableInt(reader, "ComfortRating"),
                        ValueRating = ReadNullableInt(reader, "ValueRating"),
                        WouldRecommend = ReadNullableBoolean(reader, "WouldRecommend"),

                        OverallScore = overallScore,
                        RatingScale = ratingScale,
                        CleanlinessScore = ReadNullableDecimal(reader, "CleanlinessScore"),
                        ServiceScore = ReadNullableDecimal(reader, "ServiceScore"),
                        ComfortScore = ReadNullableDecimal(reader, "ComfortScore"),
                        ValueScore = ReadNullableDecimal(reader, "ValueScore"),
                        AccuracyScore = ReadNullableDecimal(reader, "AccuracyScore"),
                        CheckinScore = ReadNullableDecimal(reader, "CheckinScore"),
                        CommunicationScore = ReadNullableDecimal(reader, "CommunicationScore"),
                        FacilitiesScore = ReadNullableDecimal(reader, "FacilitiesScore"),
                        LocationScore = ReadNullableDecimal(reader, "LocationScore"),
                        StaffScore = ReadNullableDecimal(reader, "StaffScore"),

                        IsHidden = Convert.ToBoolean(reader["IsHidden"]),
                        IsReplied = Convert.ToBoolean(reader["IsReplied"]),
                        ReplyText = ReadNullableString(reader, "ReplyText"),
                        Comments = ReadNullableString(reader, "Comments"),
                        CreatedAtUtc = ReadNullableUtcDateTime(reader, "DisplayCreatedAtUtc")
                    };
                }
            }
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

        public static bool IsValidRawToken(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length != 64)
                return false;

            for (int index = 0; index < rawToken.Length; index++)
            {
                char current = rawToken[index];

                bool isHex =
                    (current >= '0' && current <= '9') ||
                    (current >= 'a' && current <= 'f') ||
                    (current >= 'A' && current <= 'F');

                if (!isHex)
                    return false;
            }

            return true;
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[32];

            using (RandomNumberGenerator generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(bytes);
            }

            return ToHex(bytes);
        }

        private static string ComputeTokenHash(string rawToken)
        {
            byte[] tokenBytes = Encoding.UTF8.GetBytes(rawToken);

            using (SHA256 sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(tokenBytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);

            for (int index = 0; index < bytes.Length; index++)
                builder.Append(bytes[index].ToString("x2"));

            return builder.ToString();
        }

        private static void ValidateSubmission(HotelFeedbackSubmissionModel submission)
        {
            if (submission.OverallRating < 1 || submission.OverallRating > 5)
                throw new ArgumentOutOfRangeException(
                    "submission.OverallRating",
                    "Overall rating must be between 1 and 5.");

            ValidateOptionalRating(submission.CleanlinessRating, "CleanlinessRating");
            ValidateOptionalRating(submission.ServiceRating, "ServiceRating");
            ValidateOptionalRating(submission.ComfortRating, "ComfortRating");
            ValidateOptionalRating(submission.ValueRating, "ValueRating");

            if (!string.IsNullOrEmpty(submission.Comments) &&
                submission.Comments.Length > 2000)
            {
                throw new ArgumentOutOfRangeException(
                    "submission.Comments",
                    "Comments cannot be longer than 2000 characters.");
            }
        }

        private static void ValidateOptionalRating(int? value, string parameterName)
        {
            if (value.HasValue && (value.Value < 1 || value.Value > 5))
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "Rating must be between 1 and 5.");
        }

        private static void ValidateRequiredLength(
            string value,
            int maximumLength,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A value is required.", parameterName);

            if (value.Trim().Length > maximumLength)
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The value is longer than the database field.");
        }

        private static string NormalizeOptional(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string trimmed = value.Trim();

            return trimmed.Length <= maximumLength
                ? trimmed
                : trimmed.Substring(0, maximumLength);
        }

        private static string ReadNullableString(SqlDataReader reader, string columnName)
        {
            return reader[columnName] == DBNull.Value
                ? null
                : Convert.ToString(reader[columnName]);
        }

        private static int? ReadNullableInt(SqlDataReader reader, string columnName)
        {
            return reader[columnName] == DBNull.Value
                ? (int?)null
                : Convert.ToInt32(reader[columnName]);
        }

        private static bool? ReadNullableBoolean(SqlDataReader reader, string columnName)
        {
            return reader[columnName] == DBNull.Value
                ? (bool?)null
                : Convert.ToBoolean(reader[columnName]);
        }

        private static decimal? ReadNullableDecimal(SqlDataReader reader, string columnName)
        {
            return reader[columnName] == DBNull.Value
                ? (decimal?)null
                : Convert.ToDecimal(reader[columnName]);
        }

        private static DateTime? ReadNullableUtcDateTime(SqlDataReader reader, string columnName)
        {
            if (reader[columnName] == DBNull.Value)
                return null;

            DateTime value = Convert.ToDateTime(reader[columnName]);
            return value.Kind == DateTimeKind.Utc
                ? value
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static void AddNullableTinyInt(
            SqlCommand command,
            string parameterName,
            int? value)
        {
            SqlParameter parameter =
                command.Parameters.Add(parameterName, SqlDbType.TinyInt);

            parameter.Value = value.HasValue
                ? (object)value.Value
                : DBNull.Value;
        }

        private static void AddRequiredVarchar(
            SqlCommand command,
            string parameterName,
            int length,
            string value)
        {
            command.Parameters.Add(parameterName, SqlDbType.VarChar, length).Value = value;
        }

        private static void AddNullableVarchar(
            SqlCommand command,
            string parameterName,
            int length,
            string value)
        {
            command.Parameters.Add(parameterName, SqlDbType.VarChar, length).Value =
                string.IsNullOrWhiteSpace(value)
                    ? (object)DBNull.Value
                    : value;
        }

        private static void AddRequiredNvarchar(
            SqlCommand command,
            string parameterName,
            int length,
            string value)
        {
            command.Parameters.Add(parameterName, SqlDbType.NVarChar, length).Value = value;
        }

        private static void AddNullableNvarchar(
            SqlCommand command,
            string parameterName,
            int length,
            string value)
        {
            command.Parameters.Add(parameterName, SqlDbType.NVarChar, length).Value =
                string.IsNullOrWhiteSpace(value)
                    ? (object)DBNull.Value
                    : value;
        }
    }

    public sealed class HotelFeedbackReviewModel
    {
        public long FeedbackId { get; set; }
        public string HotelId { get; set; }
        public string RegId { get; set; }

        public string ReviewSource { get; set; }
        public string ExternalProvider { get; set; }
        public string ChannexReviewId { get; set; }
        public string ChannexBookingId { get; set; }
        public string OtaName { get; set; }
        public string OtaReservationId { get; set; }

        public int OverallRating { get; set; }
        public int? CleanlinessRating { get; set; }
        public int? ServiceRating { get; set; }
        public int? ComfortRating { get; set; }
        public int? ValueRating { get; set; }
        public bool? WouldRecommend { get; set; }

        public decimal OverallScore { get; set; }
        public decimal RatingScale { get; set; }
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
        public string Comments { get; set; }
        public DateTime? CreatedAtUtc { get; set; }

        public bool IsOta
        {
            get
            {
                return string.Equals(
                    ReviewSource,
                    "OTA",
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

}
