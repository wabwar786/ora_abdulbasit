using hotelsoftware.Utilities;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.Services;
using System.Web.UI;
using System.Collections.Concurrent;
using System.Threading;

namespace hotelsoftware
{
    public partial class UpdateRateValues : Page
    {
        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                string userIdBase64 = Request.QueryString["UD"];
                string userNameBase64 = Request.QueryString["UN"];
                string hotelidbase64 = Request.QueryString["hd"];

                string userId = Encoding.UTF8.GetString(Convert.FromBase64String(userIdBase64));
                string userName = Encoding.UTF8.GetString(Convert.FromBase64String(userNameBase64));
                string hotelid = Encoding.UTF8.GetString(Convert.FromBase64String(hotelidbase64));

                hdHotelId.Value = hotelid;
                hdUserId.Value = userId;
                hdUserName.Value = userName;

                Session["hotel_id"] = hdHotelId.Value;
                Session["user_id"] = hdUserId.Value;
                Session["username"] = hdUserName.Value;
            }
        }

        public class SimpleOption
        {
            public string value { get; set; }
            public string text { get; set; }
        }

        public class InitResponse
        {
            public List<SimpleOption> plans { get; set; }
            public List<SimpleOption> rooms { get; set; }
        }

        public class RangeDto
        {
            public string start { get; set; }  // yyyy-MM-dd
            public string end { get; set; }    // yyyy-MM-dd
            public decimal baseRate { get; set; }
        }

        public class PreviewRequest
        {
            public List<string> plans { get; set; }
            public List<string> rooms { get; set; }
            public List<RangeDto> ranges { get; set; }
            public List<int> days { get; set; } // 0=Sunday..6=Saturday
        }

        public class PreviewRow
        {
            public string dateRangeText { get; set; }
            public string roomTypeText { get; set; }
            public string planText { get; set; }
            public string currency { get; set; }
            public decimal adjustedRate { get; set; }
            public bool isParent { get; set; }

            public string changeType { get; set; }     // "Percentage" / "Value"
            public decimal adjustment { get; set; }    // from percentage column (10, -10, etc.)
            public decimal newRate { get; set; }       // calculated
        }

        public class SaveResponse
        {
            public bool ok { get; set; }
            public string message { get; set; }
        }
        public class BackgroundSaveResponse
        {
            public bool ok { get; set; }
            public string jobId { get; set; }
            public string message { get; set; }
        }

        public class BackgroundSaveStatus
        {
            public bool ok { get; set; }
            public string status { get; set; }
            public string message { get; set; }
            public int savedRows { get; set; }
        }

        private class RateSaveJob
        {
            public string JobId { get; set; }
            public string HotelId { get; set; }
            public string UserId { get; set; }
            public string Username { get; set; }
            public string SystemName { get; set; }
            public string IpAddress { get; set; }
            public PreviewRequest Request { get; set; }

            public string Status { get; set; }
            public string Message { get; set; }
            public int SavedRows { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        // History DTO for grid
        public class HistoryRow
        {
            public string dateCreated { get; set; }
            public string updatedBy { get; set; }
            public string ratePlan { get; set; }
            public string roomType { get; set; }
            public string days { get; set; }
            public string dateFrom { get; set; }
            public string dateTo { get; set; }
            public string baseRateSet { get; set; }
        }

        private static string ConnStr => ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        private static readonly ConcurrentDictionary<string, RateSaveJob>
    RateSaveJobs = new ConcurrentDictionary<string, RateSaveJob>();
        // ---------------- WEB METHODS ----------------

        [WebMethod(EnableSession = true)]
        public static InitResponse LoadInit()
        {
            string hotelId = GetHotelIdFromRequest();
            var resp = new InitResponse { plans = new List<SimpleOption>(), rooms = new List<SimpleOption>() };

            using (var con = new SqlConnection(ConnStr))
            {
                con.Open();

                // Plans
                using (var cmd = new SqlCommand(@"
SELECT id, name, localplanid
FROM dbo.plans
WHERE hotel_id=@hid AND ISNULL(inactive,0)=0
ORDER BY name;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            resp.plans.Add(new SimpleOption
                            {
                                value = Convert.ToString(r["localplanid"]),
                                text = Convert.ToString(r["name"])
                            });
                        }
                    }
                }

                // Room Types (from category_plan)
                using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id, category
FROM dbo.category_plan
WHERE hotel_id=@hid
ORDER BY category;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            resp.rooms.Add(new SimpleOption
                            {
                                value = Convert.ToString(r["category_id"]),
                                text = Convert.ToString(r["category"])
                            });
                        }
                    }
                }
            }

            return resp;
        }


        [WebMethod(EnableSession = true)]
        public static BackgroundSaveResponse StartSaveRates(
    PreviewRequest req)
        {
            string hotelId = "";
            string userId = "";
            string username = "";

            try
            {
                hotelId = GetHotelIdFromRequest();

                if (string.IsNullOrWhiteSpace(hotelId))
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message =
                            "Hotel session expired. Please refresh the page."
                    };
                }

                if (req == null)
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message = "Invalid saving request."
                    };
                }

                if (req.plans == null || req.plans.Count == 0)
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message =
                            "Please select at least one rate plan."
                    };
                }

                if (req.rooms == null || req.rooms.Count == 0)
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message =
                            "Please select at least one room type."
                    };
                }

                if (req.ranges == null || req.ranges.Count == 0)
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message =
                            "Please enter at least one date range."
                    };
                }

                string validationMessage;

                if (!ValidateBackgroundSaveRequest(
                    req,
                    out validationMessage))
                {
                    return new BackgroundSaveResponse
                    {
                        ok = false,
                        message = validationMessage
                    };
                }

                userId = Convert.ToString(
                    HttpContext.Current.Session["user_id"]);

                username = Convert.ToString(
                    HttpContext.Current.Session["username"]);

                RemoveExpiredRateSaveJobs();

                string jobId = Guid.NewGuid().ToString("N");

                RateSaveJob job = new RateSaveJob
                {
                    JobId = jobId,
                    HotelId = hotelId,
                    UserId = userId,
                    Username = username,
                    SystemName = Environment.MachineName,
                    IpAddress = GetClientIp(),
                    Request = req,
                    Status = "queued",
                    Message = "Rate saving is waiting to start.",
                    SavedRows = 0,
                    CreatedAt = DateTime.Now
                };

                RateSaveJobs[jobId] = job;

                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BackgroundSave_Queued",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        "JobId=" + jobId +
                        " | User=" + username +
                        " | " + BuildBulkRateLogDescription(req)
                );

                HostingEnvironment.QueueBackgroundWorkItem(
                    delegate (CancellationToken cancellationToken)
                    {
                        ProcessBackgroundRateSave(
                            jobId,
                            cancellationToken);
                    });

                return new BackgroundSaveResponse
                {
                    ok = true,
                    jobId = jobId,
                    message = "Rate saving started in background."
                };
            }
            catch (Exception ex)
            {
                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BackgroundSave_StartFailed",
                    hotelId: hotelId,
                    userId: userId,
                    description:
                        "User=" + username +
                        " | " +
                        ex.GetType().Name +
                        ": " +
                        ex.Message
                );

                return new BackgroundSaveResponse
                {
                    ok = false,
                    message =
                        "Unable to start background saving: " +
                        ex.Message
                };
            }
        }

        [WebMethod(EnableSession = true)]
        public static BackgroundSaveStatus GetSaveRatesStatus(
            string jobId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    return new BackgroundSaveStatus
                    {
                        ok = false,
                        status = "not_found",
                        message = "Saving job ID is missing."
                    };
                }

                RateSaveJob job;

                if (!RateSaveJobs.TryGetValue(jobId, out job))
                {
                    return new BackgroundSaveStatus
                    {
                        ok = false,
                        status = "not_found",
                        message =
                            "Saving job was not found. " +
                            "The application may have restarted."
                    };
                }

                string hotelId = GetHotelIdFromRequest();

                if (!string.Equals(
                    hotelId,
                    job.HotelId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new BackgroundSaveStatus
                    {
                        ok = false,
                        status = "unauthorized",
                        message =
                            "You cannot access this saving job."
                    };
                }

                return new BackgroundSaveStatus
                {
                    ok = true,
                    status = job.Status,
                    message = job.Message,
                    savedRows = job.SavedRows
                };
            }
            catch (Exception ex)
            {
                return new BackgroundSaveStatus
                {
                    ok = false,
                    status = "failed",
                    message = ex.Message
                };
            }
        }

        private static void ProcessBackgroundRateSave(
            string jobId,
            CancellationToken cancellationToken)
        {
            RateSaveJob job;

            if (!RateSaveJobs.TryGetValue(jobId, out job))
            {
                return;
            }

            try
            {
                job.Status = "processing";
                job.Message =
                    "Rates are being saved. Please do not submit again.";

                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BackgroundSave_Started",
                    hotelId: job.HotelId,
                    userId: job.UserId,
                    description:
                        "JobId=" + job.JobId +
                        " | User=" + job.Username
                );

                if (cancellationToken.IsCancellationRequested)
                {
                    job.Status = "cancelled";
                    job.Message =
                        "Saving was cancelled by the server.";
                    return;
                }

                var result =
                    RateSaveService.SaveRatesFastWithDerived(
                        connStr: ConnStr,
                        hotelId: job.HotelId,
                        username: job.Username,
                        systemName: job.SystemName,
                        ip: job.IpAddress,
                        req: job.Request,
                        out var affectedPlanIds,
                        out var affectedCategoryIds
                    );

                job.SavedRows = result.rowCount;
                job.Message =
                    "Rates saved. Creating upload history.";

                Guid batchId = Guid.NewGuid();

                try
                {
                    InsertRateUploadHistory(
                        connStr: ConnStr,
                        hotelId: job.HotelId,
                        userId: job.UserId,
                        username: job.Username,
                        pc: job.SystemName,
                        ip: job.IpAddress,
                        req: job.Request,
                        batchId: batchId
                    );
                }
                catch (Exception historyException)
                {
                    Log_helper.Log(
                        module: "Bulk Upload Rates",
                        action: "BackgroundSave_HistoryFailed",
                        hotelId: job.HotelId,
                        userId: job.UserId,
                        description:
                            "JobId=" + job.JobId +
                            " | BatchId=" + batchId +
                            " | " +
                            historyException.GetType().Name +
                            ": " +
                            historyException.Message
                    );
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    job.Status = "completed_with_warning";
                    job.Message =
                        "Rates were saved, but channel upload was cancelled.";
                    return;
                }

                job.Message =
                    "Rates saved. Uploading rates to Channel Manager.";

                try
                {
                    ChannexUploadWorker.UploadHotelRangeToChannex(
                        connStr: ConnStr,
                        hotelId: job.HotelId,
                        fromDate: result.minDate,
                        toDate: result.maxDate,
                        planIds: affectedPlanIds,
                        categoryIds: affectedCategoryIds
                    );
                }
                catch (Exception channelException)
                {
                    Log_helper.Log(
                        module: "Channex",
                        action: "BackgroundSave_ChannelUploadFailed",
                        hotelId: job.HotelId,
                        userId: job.UserId,
                        description:
                            "JobId=" + job.JobId +
                            " | BatchId=" + batchId +
                            " | " +
                            channelException.GetType().Name +
                            ": " +
                            channelException.Message
                    );

                    job.Status = "completed_with_warning";
                    job.Message =
                        "Rates saved successfully, but Channel Manager upload failed.";
                    return;
                }

                job.Status = "completed";
                job.Message =
                    "Rate saving completed successfully. " +
                    result.rowCount +
                    " rows were processed.";

                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BackgroundSave_Completed",
                    hotelId: job.HotelId,
                    userId: job.UserId,
                    description:
                        "JobId=" + job.JobId +
                        " | BatchId=" + batchId +
                        " | Rows=" + result.rowCount
                );
            }
            catch (Exception ex)
            {
                Exception actualException =
                    ex.GetBaseException() ?? ex;

                SqlException sqlException =
                    actualException as SqlException;

                string detailedError;

                if (sqlException != null)
                {
                    detailedError =
                        "Database error " +
                        sqlException.Number +
                        ": " +
                        sqlException.Message;

                    if (!string.IsNullOrWhiteSpace(
                        sqlException.Procedure))
                    {
                        detailedError +=
                            " | Procedure: " +
                            sqlException.Procedure;
                    }

                    if (sqlException.LineNumber > 0)
                    {
                        detailedError +=
                            " | SQL Line: " +
                            sqlException.LineNumber;
                    }
                }
                else
                {
                    detailedError =
                        actualException.GetType().Name +
                        ": " +
                        actualException.Message;
                }

                job.Status = "failed";

                job.Message =
                    "Rate saving failed: " +
                    detailedError +
                    " | Reference: " +
                    job.JobId;

                Log_helper.Log(
                    module: "Bulk Upload Rates",
                    action: "BackgroundSave_Failed",
                    hotelId: job.HotelId,
                    userId: job.UserId,
                    description:
                        "JobId=" + job.JobId +
                        " | User=" + job.Username +
                        " | Error=" + detailedError +
                        " | FullException=" +
                        ex.ToString()
                );
            }
        }

        private static bool ValidateBackgroundSaveRequest(
            PreviewRequest req,
            out string message)
        {
            message = "";

            foreach (RangeDto range in req.ranges)
            {
                if (range == null)
                {
                    message = "One of the date ranges is empty.";
                    return false;
                }

                DateTime startDate;
                DateTime endDate;

                if (!DateTime.TryParseExact(
                    range.start,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out startDate))
                {
                    message =
                        "Invalid start date: " +
                        range.start;
                    return false;
                }

                if (!DateTime.TryParseExact(
                    range.end,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out endDate))
                {
                    message =
                        "Invalid end date: " +
                        range.end;
                    return false;
                }

                if (endDate.Date < startDate.Date)
                {
                    message =
                        "End date cannot be earlier than start date.";
                    return false;
                }

                if (range.baseRate < 0)
                {
                    message = "Rate cannot be negative.";
                    return false;
                }
            }

            return true;
        }

        private static void RemoveExpiredRateSaveJobs()
        {
            DateTime expiryDate = DateTime.Now.AddHours(-12);

            foreach (KeyValuePair<string, RateSaveJob> item
                in RateSaveJobs)
            {
                if (item.Value.CreatedAt < expiryDate)
                {
                    RateSaveJob removedJob;

                    RateSaveJobs.TryRemove(
                        item.Key,
                        out removedJob);
                }
            }
        }



        //[WebMethod(EnableSession = true)]


        //public static SaveResponse SaveRates(PreviewRequest req)
        //{
        //    string hotelId = "";
        //    string userId = "";
        //    string username = "";

        //    try
        //    {
        //        hotelId = GetHotelIdFromRequest();
        //        userId = (HttpContext.Current?.Session?["user_id"] as string) ?? "";
        //        username = GetUserNameFromPage();
        //        string systemName = Environment.MachineName;
        //        string ip = GetClientIp();

        //        if (req == null || req.plans == null || req.rooms == null || req.ranges == null)
        //           return new SaveResponse { ok = false, message = "Invalid request" };
        //        // ✅ Log attempt
        //        Log_helper.Log(
        //            module: "Bulk Upload Rates",
        //            action: "BulkUpload_Attempt",
        //            hotelId: hotelId,
        //            userId: userId,
        //            description: $"User={username} | {BuildBulkRateLogDescription(req)}"
        //        );
        //        // ✅ Save rates (bulk + derived)
        //        var result = RateSaveService.SaveRatesFastWithDerived(
        //            connStr: ConnStr,
        //            hotelId: hotelId,
        //            username: username,
        //            systemName: systemName,
        //            ip: ip,
        //            req: req,
        //            out var affectedPlanIds,
        //            out var affectedCategoryIds
        //        );

        //        // ✅ Create history rows (like screenshot)
        //        Guid batchId = Guid.NewGuid();
        //        InsertRateUploadHistory(
        //            connStr: ConnStr,
        //            hotelId: hotelId,
        //            userId: userId,
        //            username: username,
        //            pc: systemName,
        //            ip: ip,
        //            req: req,
        //            batchId: batchId
        //        );

        //        // ✅ Log saved
        //        Log_helper.Log(
        //            module: "Bulk Upload Rates",
        //            action: "BulkUpload_Saved",
        //            hotelId: hotelId,
        //            userId: userId,
        //            description:
        //                $"User={username} | Rows={result.rowCount} | " +
        //                $"DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | " +
        //                $"AffectedPlans={affectedPlanIds?.Count ?? 0} | AffectedRooms={affectedCategoryIds?.Count ?? 0} | Batch={batchId}"
        //        );

        //        // ✅ Queue channex upload (background)
        //        Log_helper.Log(
        //            module: "Bulk Upload Rates",
        //            action: "RatesUpload_Queued",
        //            hotelId: hotelId,
        //            userId: userId,
        //            description:
        //                $"User={username} | DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | " +
        //                $"Plans={affectedPlanIds?.Count ?? 0} | Rooms={affectedCategoryIds?.Count ?? 0} | Batch={batchId}"
        //        );

        //        HostingEnvironment.QueueBackgroundWorkItem(ct =>
        //        {
        //            try
        //            {
        //                ChannexUploadWorker.UploadHotelRangeToChannex(
        //                    connStr: ConnStr,
        //                    hotelId: hotelId,
        //                    fromDate: result.minDate,
        //                    toDate: result.maxDate,
        //                    planIds: affectedPlanIds,
        //                    categoryIds: affectedCategoryIds
        //                );

        //                Log_helper.Log(
        //                    module: "Bulk Upload Rates",
        //                    action: "RatesUpload_Success",
        //                    hotelId: hotelId,
        //                    userId: userId,
        //                    description: $"User={username} | DateRange={result.minDate:yyyy-MM-dd}->{result.maxDate:yyyy-MM-dd} | Batch={batchId}"
        //                );
        //            }
        //            catch (Exception ex)
        //            {
        //                Log_helper.Log(
        //                    module: "Channex",
        //                    action: "RatesUpload_Failed",
        //                    hotelId: hotelId,
        //                    userId: userId,
        //                    description: $"User={username} | Batch={batchId} | {ex.GetType().Name}: {ex.Message}"
        //                );
        //            }
        //        });

        //        return new SaveResponse
        //        {
        //            ok = true,
        //            message = $"Saved ({result.rowCount} rows). History created. Upload queued."
        //        };
        //    }
        //    catch (Exception ex)
        //    {
        //        Log_helper.Log(
        //            module: "Bulk Upload Rates",
        //            action: "BulkUpload_Failed",
        //            hotelId: hotelId,
        //            userId: userId,
        //            description: $"User={username} | {ex.GetType().Name}: {ex.Message}"
        //        );

        //        return new SaveResponse { ok = false, message = ex.Message };
        //    }
        //}

        // ✅ Preview stays same (your code) - kept here
        //        [WebMethod(EnableSession = true)]
        //        public static object Preview(PreviewRequest req)
        //        {
        //            try
        //            {
        //                string hotelId = GetHotelIdFromRequest();
        //                if (string.IsNullOrWhiteSpace(hotelId))
        //                    return new { ok = false, error = "HotelId missing in session." };

        //                if (req == null)
        //                    return new { ok = false, error = "Request is null." };

        //                if (req.plans == null || req.rooms == null || req.ranges == null)
        //                    return new { ok = false, error = "plans/rooms/ranges missing." };

        //                var planNameByLocalPlanId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        //                var roomNameByCatId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        //                var percMap = new Dictionary<string, (decimal perc, string currency, string planName)>(StringComparer.OrdinalIgnoreCase);

        //                using (var con = new SqlConnection(ConnStr))
        //                {
        //                    con.Open();

        //                    // room names
        //                    using (var cmd = new SqlCommand(@"
        //SELECT DISTINCT category_id, category
        //FROM dbo.category_plan
        //WHERE hotel_id=@hid;", con))
        //                    {
        //                        cmd.Parameters.AddWithValue("@hid", hotelId);
        //                        using (var r = cmd.ExecuteReader())
        //                        {
        //                            while (r.Read())
        //                            {
        //                                var cid = Convert.ToString(r["category_id"]);
        //                                var ctext = Convert.ToString(r["category"]);
        //                                if (!roomNameByCatId.ContainsKey(cid))
        //                                    roomNameByCatId[cid] = ctext;
        //                            }
        //                        }
        //                    }

        //                    // percentages
        //                    using (var cmd = new SqlCommand(@"
        //SELECT category_id, localplanid, planname, currency, ISNULL(percentage,0) AS percentage
        //FROM dbo.category_plan
        //WHERE hotel_id=@hid;", con))
        //                    {
        //                        cmd.Parameters.AddWithValue("@hid", hotelId);
        //                        using (var r = cmd.ExecuteReader())
        //                        {
        //                            while (r.Read())
        //                            {
        //                                string catId = Convert.ToString(r["category_id"]);
        //                                string lp = Convert.ToString(r["localplanid"]);
        //                                string planName = Convert.ToString(r["planname"]);
        //                                string currency = Convert.ToString(r["currency"]);
        //                                decimal perc = Convert.ToDecimal(r["percentage"]);

        //                                percMap[$"{catId}|{lp}"] = (perc, currency, planName);

        //                                if (!planNameByLocalPlanId.ContainsKey(lp))
        //                                    planNameByLocalPlanId[lp] = planName;
        //                            }
        //                        }
        //                    }
        //                }

        //                var output = new List<PreviewRow>();

        //                foreach (var rg in req.ranges)
        //                {
        //                    if (rg == null) continue;

        //                    if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        //                        continue;
        //                    if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        //                        continue;

        //                    string dateRangeText = start.ToString("dd/MM/yyyy") + " - " + end.ToString("dd/MM/yyyy");

        //                    foreach (var roomId in req.rooms)
        //                    {
        //                        string roomText = roomNameByCatId.ContainsKey(roomId) ? roomNameByCatId[roomId] : roomId;

        //                        foreach (var planLp in req.plans)
        //                        {
        //                            var key = $"{roomId}|{planLp}";
        //                            decimal perc = percMap.ContainsKey(key) ? percMap[key].perc : 0m;
        //                            string currency = percMap.ContainsKey(key) ? (percMap[key].currency ?? "") : "";
        //                            string planText = percMap.ContainsKey(key) ? (percMap[key].planName ?? "") :
        //                                              (planNameByLocalPlanId.ContainsKey(planLp) ? planNameByLocalPlanId[planLp] : planLp);

        //                            decimal adjusted = rg.baseRate + (rg.baseRate * (perc / 100m));

        //                            output.Add(new PreviewRow
        //                            {
        //                                dateRangeText = dateRangeText,
        //                                roomTypeText = roomText,
        //                                planText = planText,
        //                                currency = currency,
        //                                adjustedRate = adjusted
        //                            });
        //                        }
        //                    }
        //                }

        //                return new { ok = true, rows = output };
        //            }
        //            catch (Exception ex)
        //            {
        //                return new { ok = false, error = ex.Message };
        //            }
        //        }

        // ✅ Load Rate Upload History (for your screenshot grid)

        [WebMethod(EnableSession = true)]
        public static object Preview(PreviewRequest req)
        {
            try
            {
                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, error = "HotelId missing in session." };

                if (req == null)
                    return new { ok = false, error = "Request is null." };

                if (req.plans == null || req.rooms == null || req.ranges == null)
                    return new { ok = false, error = "plans/rooms/ranges missing." };

                var planNameByLocalPlanId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var roomNameByCatId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // key: "{catId}|{localplanid}" -> (amountOrPerc, currency, planName, changeType)
                var adjMap = new Dictionary<string, (decimal amountOrPerc, string currency, string planName, string changeType)>(StringComparer.OrdinalIgnoreCase);

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();

                    // room names
                    using (var cmd = new SqlCommand(@"
SELECT DISTINCT category_id, category
FROM dbo.category_plan
WHERE hotel_id=@hid;", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                var cid = Convert.ToString(r["category_id"]);
                                var ctext = Convert.ToString(r["category"]);
                                if (!roomNameByCatId.ContainsKey(cid))
                                    roomNameByCatId[cid] = ctext;
                            }
                        }
                    }

                    // adjustments (percentage/value)
                    using (var cmd = new SqlCommand(@"
SELECT category_id,
       localplanid,
       planname,
       currency,
       ISNULL(percentage,0) AS percentage,
       ISNULL(changetype,'Percentage') AS changetype
FROM dbo.category_plan
WHERE hotel_id=@hid;", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string catId = Convert.ToString(r["category_id"]);
                                string lp = Convert.ToString(r["localplanid"]);
                                string planName = Convert.ToString(r["planname"]);
                                string currency = Convert.ToString(r["currency"]);
                                decimal amountOrPerc = Convert.ToDecimal(r["percentage"]); // reuse your existing column
                                string changeType = Convert.ToString(r["changetype"]) ?? "Percentage";

                                adjMap[$"{catId}|{lp}"] = (amountOrPerc, currency, planName, changeType);

                                if (!planNameByLocalPlanId.ContainsKey(lp))
                                    planNameByLocalPlanId[lp] = planName;
                            }
                        }
                    }
                }

              

                var output = new List<PreviewRow>();

                foreach (var rg in req.ranges)
                {
                    if (rg == null) continue;

                    if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                        continue;
                    if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                        continue;

                    string dateRangeText = start.ToString("dd/MM/yyyy") + " - " + end.ToString("dd/MM/yyyy");

                    foreach (var roomId in req.rooms)
                    {
                        string roomText = roomNameByCatId.ContainsKey(roomId) ? roomNameByCatId[roomId] : roomId;

                        foreach (var planLp in req.plans)
                        {
                            var key = $"{roomId}|{planLp}";

                            decimal amountOrPerc = 0m;
                            string currency = "";
                            string planText = "";
                            if (adjMap.TryGetValue(key, out var adj))
                            {
                                amountOrPerc = adj.amountOrPerc;
                                currency = adj.currency ?? "";
                                planText = !string.IsNullOrWhiteSpace(adj.planName) ? adj.planName : planLp;
                            }
                            else
                            {
                                planText = planNameByLocalPlanId.ContainsKey(planLp) ? planNameByLocalPlanId[planLp] : planLp;
                            }
                            // ✅ Apply Value or Percentage
                            decimal adjusted;
                            string changeType = adjMap.TryGetValue(key, out var adj2) ? (adj2.changeType ?? "Percentage") : "Percentage";
                            if (IsValueType(changeType))
                            {
                                // Value means: base + value
                                adjusted = rg.baseRate + amountOrPerc;
                            }
                            else
                            {
                                adjusted = rg.baseRate + (rg.baseRate * (amountOrPerc / 100m));
                            }
                            output.Add(new PreviewRow
                            {
                                dateRangeText = dateRangeText,
                                roomTypeText = roomText,
                                planText = planText,
                                currency = currency,
                                adjustedRate = adjusted
                            });
                        }
                    }
                }

                return new { ok = true, rows = output };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message };
            }
        }
        // helper: normalize changetype
        static bool IsValueType(string t)
        {
            t = (t ?? "").Trim().ToLowerInvariant();
            return t == "value" || t == "v" || t == "amount" || t == "fixed" || t == "flat";
        }
        static bool IsPercentType(string t)
        {
            t = (t ?? "").Trim().ToLowerInvariant();
            return t == "percentage" || t == "percent" || t == "perc" || t == "%";
        }
        [WebMethod(EnableSession = true)]
        public static object LoadRateUploadHistory(string search, string from, string to, int page, int pageSize)
        {
            try
            {
                string hotelId = GetHotelIdFromRequest();
                if (string.IsNullOrWhiteSpace(hotelId))
                    return new { ok = false, error = "HotelId missing." };

                DateTime? fromDt = null, toDt = null;

                // If your date inputs are dd/MM/yyyy in UI
                if (!string.IsNullOrWhiteSpace(from) &&
                    DateTime.TryParseExact(from, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f))
                    fromDt = f.Date;

                if (!string.IsNullOrWhiteSpace(to) &&
                    DateTime.TryParseExact(to, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                    toDt = t.Date;

                if (page <= 0) page = 1;
                if (pageSize <= 0) pageSize = 15;
                int offset = (page - 1) * pageSize;

                int total = 0;
                var rows = new List<HistoryRow>();

                using (var con = new SqlConnection(ConnStr))
                {
                    con.Open();

                    using (var cmd = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.RateUploadHistoryTB h
WHERE h.hotel_id=@hid
  AND (@from IS NULL OR h.created_at >= @from)
  AND (@to IS NULL OR h.created_at < DATEADD(DAY,1,@to))
  AND (
        @search IS NULL OR @search='' OR
        h.plan_name LIKE '%'+@search+'%' OR
        h.room_type LIKE '%'+@search+'%' OR
        h.updated_by LIKE '%'+@search+'%'
      );", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@from", (object)fromDt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@to", (object)toDt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@search", (object)search ?? "");
                        total = Convert.ToInt32(cmd.ExecuteScalar());
                    }

                    using (var cmd = new SqlCommand(@"
SELECT
  h.created_at,
  h.updated_by,
  h.plan_name,
  h.room_type,
  h.days_text,
  h.date_from,
  h.date_to,
  h.base_rate_set,
  h.currency
FROM dbo.RateUploadHistoryTB h
WHERE h.hotel_id=@hid
  AND (@from IS NULL OR h.created_at >= @from)
  AND (@to IS NULL OR h.created_at < DATEADD(DAY,1,@to))
  AND (
        @search IS NULL OR @search='' OR
        h.plan_name LIKE '%'+@search+'%' OR
        h.room_type LIKE '%'+@search+'%' OR
        h.updated_by LIKE '%'+@search+'%'
      )
ORDER BY h.created_at DESC, h.id DESC
OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY;", con))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        cmd.Parameters.AddWithValue("@from", (object)fromDt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@to", (object)toDt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@search", (object)search ?? "");
                        cmd.Parameters.AddWithValue("@offset", offset);
                        cmd.Parameters.AddWithValue("@size", pageSize);

                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                var created = Convert.ToDateTime(r["created_at"]);
                                var cur = Convert.ToString(r["currency"]);
                                var baseRate = Convert.ToDecimal(r["base_rate_set"]);

                                rows.Add(new HistoryRow
                                {
                                    dateCreated = created.ToString("dd/MM/yyyy HH:mm"),
                                    updatedBy = Convert.ToString(r["updated_by"]),
                                    ratePlan = Convert.ToString(r["plan_name"]),
                                    roomType = Convert.ToString(r["room_type"]),
                                    days = Convert.ToString(r["days_text"]),
                                    dateFrom = Convert.ToDateTime(r["date_from"]).ToString("dd/MM/yyyy"),
                                    dateTo = Convert.ToDateTime(r["date_to"]).ToString("dd/MM/yyyy"),
                                    baseRateSet = (string.IsNullOrWhiteSpace(cur) ? "" : cur) + baseRate.ToString("0.00")
                                });
                            }
                        }
                    }
                }

                return new { ok = true, total = total, rows = rows };
            }
            catch (Exception ex)
            {
                return new { ok = false, error = ex.Message };
            }
        }
        // ---------------- HELPERS ----------------
        private static string BuildBulkRateLogDescription(PreviewRequest req)
        {
            if (req == null) return "req=null";
            string plans = req.plans != null ? string.Join(",", req.plans.Take(50)) : "";
            string rooms = req.rooms != null ? string.Join(",", req.rooms.Take(50)) : "";
            string days = req.days != null ? string.Join(",", req.days) : "";
            int rangesCount = req.ranges?.Count ?? 0;

            return $"Plans({req.plans?.Count ?? 0})=[{plans}] | Rooms({req.rooms?.Count ?? 0})=[{rooms}] | Ranges={rangesCount} | Days=[{days}]";
        }

        private static void InsertRateUploadHistory(
            string connStr,
            string hotelId,
            string userId,
            string username,
            string pc,
            string ip,
            PreviewRequest req,
            Guid batchId)
        {
            // Convert days list to text like screenshot
            string daysText = "All";
            if (req.days != null && req.days.Count > 0 && req.days.Count < 7)
            {
                string[] names = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
                daysText = string.Join(",", req.days.Distinct().OrderBy(x => x)
                    .Select(d => names[Math.Max(0, Math.Min(6, d))]));
            }

            var planNameByLocalPlanId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roomNameByCatId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var currencyByCatPlan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // cat|plan -> currency

            using (var con = new SqlConnection(connStr))
            {
                con.Open();

                // plan names
                using (var cmd = new SqlCommand(@"
SELECT localplanid, name
FROM dbo.plans
WHERE hotel_id=@hid AND ISNULL(inactive,0)=0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"]);
                            var nm = Convert.ToString(r["name"]);
                            if (!planNameByLocalPlanId.ContainsKey(lp))
                                planNameByLocalPlanId[lp] = nm;
                        }
                    }
                }

                // room names + currency
                using (var cmd = new SqlCommand(@"
SELECT category_id, category, localplanid, currency
FROM dbo.category_plan
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var cid = Convert.ToString(r["category_id"]);
                            var ctext = Convert.ToString(r["category"]);
                            var lp = Convert.ToString(r["localplanid"]);
                            var cur = Convert.ToString(r["currency"]);

                            if (!roomNameByCatId.ContainsKey(cid))
                                roomNameByCatId[cid] = ctext;

                            currencyByCatPlan[$"{cid}|{lp}"] = cur;
                        }
                    }
                }

                using (var tx = con.BeginTransaction())
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.RateUploadHistoryTB
(
  hotel_id, batch_id, created_at, user_id, updated_by, pc, ip,
  plan_id, plan_name, category_id, room_type, days_text,
  date_from, date_to, base_rate_set, currency
)
VALUES
(
  @hotel_id, @batch_id, GETDATE(), @user_id, @updated_by, @pc, @ip,
  @plan_id, @plan_name, @category_id, @room_type, @days_text,
  @date_from, @date_to, @base_rate_set, @currency
);", con, tx))
                {
                    cmd.Parameters.Add("@hotel_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@batch_id", SqlDbType.UniqueIdentifier);
                    cmd.Parameters.Add("@user_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@updated_by", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@pc", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@ip", SqlDbType.NVarChar, 100);

                    cmd.Parameters.Add("@plan_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@plan_name", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@category_id", SqlDbType.NVarChar, 50);
                    cmd.Parameters.Add("@room_type", SqlDbType.NVarChar, 200);
                    cmd.Parameters.Add("@days_text", SqlDbType.NVarChar, 50);

                    cmd.Parameters.Add("@date_from", SqlDbType.Date);
                    cmd.Parameters.Add("@date_to", SqlDbType.Date);
                    cmd.Parameters.Add("@base_rate_set", SqlDbType.Decimal).Precision = 18;
                    cmd.Parameters["@base_rate_set"].Scale = 2;
                    cmd.Parameters.Add("@currency", SqlDbType.NVarChar, 20);

                    foreach (var rg in req.ranges ?? new List<RangeDto>())
                    {
                        if (rg == null) continue;

                        if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                            continue;
                        if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                            continue;

                        foreach (var roomId in req.rooms ?? new List<string>())
                        {
                            var roomText = roomNameByCatId.ContainsKey(roomId) ? roomNameByCatId[roomId] : roomId;

                            foreach (var planLp in req.plans ?? new List<string>())
                            {
                                var planText = planNameByLocalPlanId.ContainsKey(planLp) ? planNameByLocalPlanId[planLp] : planLp;
                                var cur = currencyByCatPlan.TryGetValue($"{roomId}|{planLp}", out var c) ? c : "";

                                cmd.Parameters["@hotel_id"].Value = hotelId ?? "";
                                cmd.Parameters["@batch_id"].Value = batchId;
                                cmd.Parameters["@user_id"].Value = userId ?? "";
                                cmd.Parameters["@updated_by"].Value = username ?? "";
                                cmd.Parameters["@pc"].Value = pc ?? "";
                                cmd.Parameters["@ip"].Value = ip ?? "";

                                cmd.Parameters["@plan_id"].Value = planLp ?? "";
                                cmd.Parameters["@plan_name"].Value = planText ?? "";
                                cmd.Parameters["@category_id"].Value = roomId ?? "";
                                cmd.Parameters["@room_type"].Value = roomText ?? "";
                                cmd.Parameters["@days_text"].Value = daysText ?? "All";

                                cmd.Parameters["@date_from"].Value = start.Date;
                                cmd.Parameters["@date_to"].Value = end.Date;
                                cmd.Parameters["@base_rate_set"].Value = rg.baseRate;
                                cmd.Parameters["@currency"].Value = cur ?? "";

                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    tx.Commit();
                }
            }
        }

        private static string GetHotelIdFromRequest()
        {
            var ctx = HttpContext.Current;

            var hotelId = ctx.Session?["hotel_id"] as string;
            if (!string.IsNullOrWhiteSpace(hotelId))
                return hotelId;

            string hd = ctx.Request.QueryString["hd"];
            if (string.IsNullOrWhiteSpace(hd))
                return "";

            return Encoding.UTF8.GetString(Convert.FromBase64String(hd));
        }

        private static string GetUserNameFromPage()
        {
            var ctx = HttpContext.Current;
            var name = ctx.Session?["username"] as string;
            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }

        private static string GetClientIp()
        {
            try
            {
                var ctx = HttpContext.Current;
                string ip = ctx?.Request?.ServerVariables["HTTP_X_FORWARDED_FOR"];
                if (!string.IsNullOrWhiteSpace(ip))
                    return ip.Split(',')[0].Trim();

                ip = ctx?.Request?.ServerVariables["REMOTE_ADDR"];
                return ip ?? "";
            }
            catch { return ""; }
        }
    }
}
