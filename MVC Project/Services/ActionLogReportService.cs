using System;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>Read-only ActionLogTB report, scoped by authenticated hotel or SuperUser.</summary>
public sealed class ActionLogReportService
{
    private readonly string _connectionString;

    public ActionLogReportService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("con")
            ?? throw new InvalidOperationException("ConnectionStrings:con is not configured.");
    }

    // ActionLogTB.date is written as the varchar value MM-dd-yyyy (style 110).
    // Single-day requests use direct equality so an index on (hotel_id,date,ID) can be used.
    // Historical ranges require TRY_CONVERT to compare across months and years correctly.
    private static string DateWhere(ActionLogReportViewModel f) => f.From == f.To
        ? "l.[date] = @DateText"
        : "TRY_CONVERT(date, l.[date], 110) BETWEEN @FromDate AND @ToDate";

    private static string SourceSql(ActionLogReportViewModel f) => $@"
FROM dbo.ActionLogTB AS l
OUTER APPLY (
    SELECT TOP (1) h.username
    FROM dbo.Hms_accounts AS h
    WHERE CONVERT(varchar(100), h.user_id) = CONVERT(varchar(100), l.userid)
    ORDER BY CASE WHEN CONVERT(varchar(100), h.hotel_id) = CONVERT(varchar(100), l.hotel_id)
                  THEN 0 ELSE 1 END
) AS account
WHERE {DateWhere(f)}
  AND (@IsSuperUser = 1 OR l.hotel_id = @SessionHotelId)
  AND (@SelectedHotelId = '' OR l.hotel_id = @SelectedHotelId)
  AND (@Module = '' OR l.module = @Module)
  AND (@Action = '' OR l.action LIKE @ActionLike)
  AND (@Search = '' OR
        CONVERT(nvarchar(100), l.ID) LIKE @SearchLike OR
        CONVERT(nvarchar(250), l.module) LIKE @SearchLike OR
        CONVERT(nvarchar(250), l.action) LIKE @SearchLike OR
        CONVERT(nvarchar(150), l.userid) LIKE @SearchLike OR
        CONVERT(nvarchar(250), account.username) LIKE @SearchLike OR
        CONVERT(nvarchar(max), l.description) LIKE @SearchLike OR
        CONVERT(nvarchar(150), l.ip) LIKE @SearchLike OR
        CONVERT(nvarchar(200), l.pc) LIKE @SearchLike OR
        CONVERT(nvarchar(100), l.hotel_id) LIKE @SearchLike)
";

    private static string EscapeLike(string input) => input
        .Replace("[", "[[]", StringComparison.Ordinal)
        .Replace("%", "[%]", StringComparison.Ordinal)
        .Replace("_", "[_]", StringComparison.Ordinal);

    private static void Bind(SqlCommand cmd, ActionLogReportViewModel f, string sessionHotel)
    {
        cmd.CommandTimeout = 40;
        cmd.Parameters.Add("@DateText", SqlDbType.VarChar, 10).Value =
            f.From.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
        cmd.Parameters.Add("@FromDate", SqlDbType.Date).Value = f.From;
        cmd.Parameters.Add("@ToDate", SqlDbType.Date).Value = f.To;
        cmd.Parameters.Add("@SessionHotelId", SqlDbType.VarChar, 100).Value = sessionHotel;
        cmd.Parameters.Add("@SelectedHotelId", SqlDbType.VarChar, 100).Value =
            f.IsSuperUser ? f.HotelId : "";
        cmd.Parameters.Add("@IsSuperUser", SqlDbType.Bit).Value = f.IsSuperUser;
        cmd.Parameters.Add("@Module", SqlDbType.NVarChar, 200).Value = f.Module;
        cmd.Parameters.Add("@Action", SqlDbType.NVarChar, 200).Value = f.Action;
        cmd.Parameters.Add("@ActionLike", SqlDbType.NVarChar, 410).Value = "%" + EscapeLike(f.Action) + "%";
        cmd.Parameters.Add("@Search", SqlDbType.NVarChar, 200).Value = f.Search;
        cmd.Parameters.Add("@SearchLike", SqlDbType.NVarChar, 410).Value = "%" + EscapeLike(f.Search) + "%";
    }

    public async Task LoadAsync(ActionLogReportViewModel f, string sessionHotel,
        CancellationToken cancellationToken)
    {
        await using var cn = new SqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);

        // Avoid duplicates even if multiple Hms_accounts rows share a user_id.
        // Count and page use identical WHERE/JOIN clauses.
        await using (var count = new SqlCommand(
            "SELECT COUNT_BIG(1) " + SourceSql(f), cn))
        {
            Bind(count, f, sessionHotel);
            f.TotalRows = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        f.Page = (int)Math.Min(f.Page, Math.Min(int.MaxValue, f.Pages));
        await using (var cmd = new SqlCommand(@"
SELECT l.ID, l.module, l.action, l.userid,
       CASE WHEN NULLIF(LTRIM(RTRIM(account.username)), '') IS NOT NULL
            THEN LTRIM(RTRIM(account.username))
            WHEN NULLIF(LTRIM(RTRIM(l.userid)), '') IS NULL THEN 'Unknown'
            ELSE LTRIM(RTRIM(l.userid)) END AS DisplayUserName,
       l.[date], l.[time], l.pc, l.ip, l.hotel_id, l.description
" + SourceSql(f) + @"
ORDER BY l.ID DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;", cn))
        {
            Bind(cmd, f, sessionHotel);
            // int arithmetic is bounded by Page and PageSize above.
            cmd.Parameters.Add("@Offset", SqlDbType.BigInt).Value = (long)(f.Page - 1) * f.PageSize;
            cmd.Parameters.Add("@PageSize", SqlDbType.Int).Value = f.PageSize;
            await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                f.Rows.Add(new ActionLogReportRow
                {
                    Id = r.IsDBNull(0) ? 0 : Convert.ToInt64(r.GetValue(0)),
                    Module = Read(r, 1), Action = Read(r, 2), UserId = Read(r, 3),
                    UserName = Read(r, 4), Date = Read(r, 5), Time = Read(r, 6),
                    Pc = Read(r, 7), Ip = Read(r, 8), HotelId = Read(r, 9),
                    Description = Read(r, 10)
                });
            }
        }

        // Module options reflect the chosen date range and the user's permitted scope.
        await using (var modules = new SqlCommand($@"
SELECT DISTINCT TOP (100) LTRIM(RTRIM(l.module)) AS Module
FROM dbo.ActionLogTB AS l
WHERE {DateWhere(f)}
  AND (@IsSuperUser = 1 OR l.hotel_id = @SessionHotelId)
  AND (@SelectedHotelId = '' OR l.hotel_id = @SelectedHotelId)
  AND NULLIF(LTRIM(RTRIM(l.module)), '') IS NOT NULL
ORDER BY Module;", cn))
        {
            modules.CommandTimeout = 30;
            modules.Parameters.Add("@DateText", SqlDbType.VarChar, 10).Value = f.From.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
            modules.Parameters.Add("@FromDate", SqlDbType.Date).Value = f.From;
            modules.Parameters.Add("@ToDate", SqlDbType.Date).Value = f.To;
            modules.Parameters.Add("@IsSuperUser", SqlDbType.Bit).Value = f.IsSuperUser;
            modules.Parameters.Add("@SessionHotelId", SqlDbType.VarChar, 100).Value = sessionHotel;
            modules.Parameters.Add("@SelectedHotelId", SqlDbType.VarChar, 100).Value = f.IsSuperUser ? f.HotelId : "";
            await using var r = await modules.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
                f.Modules.Add(Read(r, 0));
        }

        if (f.IsSuperUser)
        {
            await using var hotels = new SqlCommand(@"
SELECT DISTINCT CONVERT(varchar(100), hotel_id) AS HotelId,
       COALESCE(NULLIF(LTRIM(RTRIM(name)), ''), CONVERT(varchar(100), hotel_id)) AS HotelName
FROM dbo.HotelsSignUpTB
WHERE NULLIF(LTRIM(RTRIM(CONVERT(varchar(100), hotel_id))), '') IS NOT NULL
ORDER BY HotelName;", cn);
            hotels.CommandTimeout = 20;
            await using var r = await hotels.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
                f.Hotels.Add(new ActionLogHotelOption { Id = Read(r, 0), Name = Read(r, 1) });
        }
    }

    private static string Read(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal)) ?? "";
}
