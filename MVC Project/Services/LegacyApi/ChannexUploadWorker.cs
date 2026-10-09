using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
namespace Orapmshms.Services.LegacyApi
{
    public static class ChannexUploadWorker
    {
        public static string channexApiKey;
        public static string channexBaseUrl;
        private static readonly HttpClient _http = new HttpClient();
        // =========================================================
        // PUBLIC: Upload RATES (your existing, fixed endpoint)
        // =========================================================
        public static void UploadHotelRangeToChannex(
            string connStr,
            string hotelId,
            DateTime fromDate,
            DateTime toDate,
            IEnumerable<string> planIds,
            IEnumerable<string> categoryIds)
        {
            string propertyId = GetChannexPropertyId(connStr, hotelId);
            if (string.IsNullOrWhiteSpace(propertyId)) return;

            LoadChannexBaseUrl(hotelId, connStr);
            channexApiKey = GetApiKey(connStr);

            if (string.IsNullOrWhiteSpace(channexBaseUrl)) return;
            if (string.IsNullOrWhiteSpace(channexApiKey)) return;

            var localCatToChRoomTypeId = GetChRoomTypeByLocalCategory(connStr, hotelId);
            var planKeyToChPlanId = GetChPlanIdByLocalPlanAndChRoomType(connStr, hotelId);

            var planSet = new HashSet<string>(planIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catSet = new HashSet<string>(categoryIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var pending = new List<(string LocalPlanId, string LocalCategoryId, DateTime Date, decimal Rate)>();

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT dr.planid      AS localplanid,
       dr.category_id AS localcategoryid,
       dr.[date],
       CAST(dr.rate AS decimal(18,2)) AS rate
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hid
  AND dr.[date] >= @fromDate AND dr.[date] <= @toDate
  AND dr.upload = 0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"])?.Trim();
                            var lc = Convert.ToString(r["localcategoryid"])?.Trim();
                            if (string.IsNullOrWhiteSpace(lp) || string.IsNullOrWhiteSpace(lc)) continue;

                            if (planSet.Count > 0 && !planSet.Contains(lp)) continue;
                            if (catSet.Count > 0 && !catSet.Contains(lc)) continue;

                            pending.Add((lp, lc, Convert.ToDateTime(r["date"]), Convert.ToDecimal(r["rate"])));
                        }
                    }
                }
            }

            if (pending.Count == 0) return;

            var values = new List<object>(pending.Count);

            foreach (var x in pending)
            {
                string chRoomTypeId = ResolveChRoomTypeId(localCatToChRoomTypeId, x.LocalCategoryId);

                if (string.IsNullOrWhiteSpace(chRoomTypeId))
                    continue;

                string chPlanId = ResolveChPlanId(planKeyToChPlanId, x.LocalPlanId, chRoomTypeId, x.LocalCategoryId);
                if (string.IsNullOrWhiteSpace(chPlanId))
                    continue;

                values.Add(new
                {
                    property_id = propertyId,
                    rate_plan_id = chPlanId,
                    room_type_id = chRoomTypeId,
                    date_from = x.Date.ToString("yyyy-MM-dd"),
                    date_to = x.Date.ToString("yyyy-MM-dd"),
                    rate = Math.Round(x.Rate, 2).ToString(CultureInfo.InvariantCulture)
                });
            }

            if (values.Count == 0) return;

            const int batchSize = 300;
            foreach (var batch in Chunk(values, batchSize))
                UploadToChannexBatch(batch, "/api/v1/restrictions"); // ✅ rates endpoint

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
UPDATE dbo.datesrates
SET upload=1
WHERE hotel_id=@hid
  AND [date] >= @fromDate AND [date] <= @toDate
  AND upload=0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // =========================================================
        // ✅ NEW: Upload RESTRICTIONS (like rates)
        // =========================================================
        public static void UploadHotelRestrictionsRangeToChannex(
     string connStr,
     string hotelId,
     DateTime fromDate,
     DateTime toDate,
     IEnumerable<string> planIds,
     IEnumerable<string> categoryIds)
        {
            // 1) Resolve Channex property + credentials
            string propertyId = GetChannexPropertyId(connStr, hotelId);
            if (string.IsNullOrWhiteSpace(propertyId)) return;

            LoadChannexBaseUrl(hotelId, connStr);
            channexApiKey = GetApiKey(connStr);

            if (string.IsNullOrWhiteSpace(channexBaseUrl)) return;
            if (string.IsNullOrWhiteSpace(channexApiKey)) return;

            // 2) Mapping dictionaries
            var localCatToChRoomTypeId = GetChRoomTypeByLocalCategory(connStr, hotelId);
            var planKeyToChPlanId = GetChPlanIdByLocalPlanAndChRoomType(connStr, hotelId);

            var planSet = new HashSet<string>(planIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var catSet = new HashSet<string>(categoryIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // 3) Read pending restriction rows
            // IMPORTANT:
            // - We DO NOT send "min_stay": null / "max_stay": null / "cutoff": null
            // - We DO NOT send "cutoff" at all (invalid for Channex restrictions payload)
            var pending = new List<(string LocalPlanId, string LocalCategoryId, DateTime Date, int? MinLos, int? MaxLos, bool? StopSell)>();

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT
    dr.planid      AS localplanid,
    dr.category_id AS localcategoryid,
    dr.[date],
    dr.min_los,
    dr.max_los,
    dr.stop_sell
FROM dbo.datesrates dr
WHERE dr.hotel_id=@hid
  AND dr.[date] >= @fromDate AND dr.[date] <= @toDate
  AND dr.restr_upload = 0", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var lp = Convert.ToString(r["localplanid"])?.Trim();
                            var lc = Convert.ToString(r["localcategoryid"])?.Trim();
                            if (string.IsNullOrWhiteSpace(lp) || string.IsNullOrWhiteSpace(lc)) continue;

                            if (planSet.Count > 0 && !planSet.Contains(lp)) continue;
                            if (catSet.Count > 0 && !catSet.Contains(lc)) continue;

                            int? minLos = r["min_los"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["min_los"]);
                            int? maxLos = r["max_los"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["max_los"]);
                            bool? stopSell = r["stop_sell"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(r["stop_sell"]);

                            pending.Add((lp, lc, Convert.ToDateTime(r["date"]).Date, minLos, maxLos, stopSell));
                        }
                    }
                }
            }
            if (pending.Count == 0) return;
            // 4) Build Channex payload (NO NULL KEYS)
            // Use the correct Channex keys:
            // - stop_sell (bool)
            // - min_stay_arrival (int)  <-- best match for your min_los
            // - max_stay (int)
            // NOTE: Some setups may prefer min_stay_through instead of min_stay_arrival.
            // If your Postman example uses min_stay_through, swap it below.
            var values = new List<Dictionary<string, object>>(pending.Count);
            foreach (var x in pending)
            {
                string chRoomTypeId = ResolveChRoomTypeId(localCatToChRoomTypeId, x.LocalCategoryId);
                if (string.IsNullOrWhiteSpace(chRoomTypeId))
                    continue;

                string chPlanId = ResolveChPlanId(planKeyToChPlanId, x.LocalPlanId, chRoomTypeId, x.LocalCategoryId);
                if (string.IsNullOrWhiteSpace(chPlanId))
                    continue;

                var item = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["property_id"] = propertyId,
                    ["rate_plan_id"] = chPlanId,
                    ["room_type_id"] = chRoomTypeId,
                    // Per-day update: safest & always accepted
                    ["date"] = x.Date.ToString("yyyy-MM-dd")
                };

                // Add only if they have values (NO nulls)
                AddIf(item, "stop_sell", x.StopSell);
                AddIf(item, "min_stay_arrival", x.MinLos); // <-- min_los -> min_stay_arrival
                AddIf(item, "max_stay", x.MaxLos);

                // If nothing to update (rare), skip
                if (item.Count <= 4) // only ids+date present
                    continue;

                values.Add(item);
            }

            if (values.Count == 0) return;

            // 5) Upload in batches
            const int batchSize = 300;
            foreach (var batch in Chunk(values.Cast<object>().ToList(), batchSize))
            {
                // Your existing uploader should serialize:
                // { "values": [ ... ] }
                UploadToChannexBatch(batch, "/api/v1/restrictions");
            }

            // 6) Mark as uploaded (same window)
            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
UPDATE dbo.datesrates
SET restr_upload=1
WHERE hotel_id=@hid
  AND [date] >= @fromDate AND [date] <= @toDate
  AND restr_upload=0;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    cmd.Parameters.AddWithValue("@fromDate", fromDate.Date);
                    cmd.Parameters.AddWithValue("@toDate", toDate.Date);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ----------------- helpers -----------------

        private static void AddIf(Dictionary<string, object> dict, string key, int? value)
        {
            if (value.HasValue) dict[key] = value.Value;
        }

        private static void AddIf(Dictionary<string, object> dict, string key, bool? value)
        {
            if (value.HasValue) dict[key] = value.Value;
        }


        // ----------------- Upload helper -----------------
        private static void UploadToChannexBatch(IReadOnlyList<object> values, string apiPath)
        {
            // TLS 1.2 (important for many servers)
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            if (values == null || values.Count == 0) return;

            var payload = new { values };
            string json = JsonConvert.SerializeObject(payload);

            string url = channexBaseUrl.TrimEnd('/') + apiPath;

            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Clear();
                req.Headers.Add("user-api-key", channexApiKey);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                var resp = _http.SendAsync(req).GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception("Channex batch upload failed: " +
                        (int)resp.StatusCode + " " + resp.ReasonPhrase + " " + body);
                }
            }
        }

        // ----------------- PROPERTY ID -----------------
        private static string GetChannexPropertyId(string connStr, string hotelId)
        {
            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT TOP 1 property_id
FROM dbo.HotelsSignUpTB
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    return Convert.ToString(cmd.ExecuteScalar());
                }
            }
        }

        // ----------------- BASE URL (staging/live) -----------------
        private static void LoadChannexBaseUrl(string hotel_id, string constr)
        {
            try
            {
                bool isStaging = false;

                using (SqlConnection conn = new SqlConnection(constr))
                {
                    conn.Open();

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT channexstaging FROM HotelsSignUpTB WHERE hotel_id=@hid", conn))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotel_id);
                        var obj = cmd.ExecuteScalar();
                        if (obj != null && obj != DBNull.Value)
                            bool.TryParse(obj.ToString(), out isStaging);
                    }

                    // Your existing logic kept as-is
                    string channelName = isStaging ? "app" : "staging";

                    using (SqlCommand cmd = new SqlCommand(
                        "SELECT TOP 1 link FROM channexlink WHERE channelname=@channelname", conn))
                    {
                        cmd.Parameters.AddWithValue("@channelname", channelName);
                        var linkObj = cmd.ExecuteScalar();
                        if (linkObj != null && linkObj != DBNull.Value)
                            channexBaseUrl = linkObj.ToString();
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // ----------------- API KEY -----------------
        private static string GetApiKey(string constr)
        {
            string apiKey = null;

            using (var connection = new SqlConnection(constr))
            {
                connection.Open();
                using (var cmd = new SqlCommand("SELECT TOP 1 * FROM channelmanagerapikey ORDER BY id DESC", connection))
                using (var sdr = cmd.ExecuteReader())
                {
                    if (sdr.Read())
                    {
                        if (string.Equals(channexBaseUrl?.TrimEnd('/'), "https://app.channex.io", StringComparison.OrdinalIgnoreCase))
                            apiKey = sdr["apikey"]?.ToString();
                        else
                            apiKey = sdr["username"]?.ToString();
                    }
                }
            }

            return apiKey;
        }

        // ----------------- MAPPINGS -----------------
        private static Dictionary<string, string> GetChRoomTypeByLocalCategory(string connStr, string hotelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT localcategoryid, category_id
FROM dbo.create_room
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var localCat = Convert.ToString(r["localcategoryid"])?.Trim();
                            var chRoomType = Convert.ToString(r["category_id"])?.Trim();

                            if (!string.IsNullOrWhiteSpace(localCat) && !string.IsNullOrWhiteSpace(chRoomType))
                                map[localCat] = chRoomType;
                        }
                    }
                }
            }

            return map;
        }

        private static Dictionary<string, string> GetChPlanIdByLocalPlanAndChRoomType(string connStr, string hotelId)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var cmd = new SqlCommand(@"
SELECT localplanid, category_id, plainid
FROM dbo.category_plan
WHERE hotel_id=@hid;", con))
                {
                    cmd.Parameters.AddWithValue("@hid", hotelId);

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var localPlan = Convert.ToString(r["localplanid"])?.Trim();
                            var chRoomType = Convert.ToString(r["category_id"])?.Trim();
                            var chPlanId = Convert.ToString(r["plainid"])?.Trim();

                            if (string.IsNullOrWhiteSpace(localPlan) ||
                                string.IsNullOrWhiteSpace(chRoomType) ||
                                string.IsNullOrWhiteSpace(chPlanId))
                                continue;

                            map[MakePlanKey(localPlan, chRoomType)] = chPlanId;
                        }
                    }
                }
            }

            return map;
        }

        private static string MakePlanKey(string localPlanId, string chRoomTypeId)
            => (localPlanId ?? "").Trim() + "||" + (chRoomTypeId ?? "").Trim();

        // ----------------- RESOLVE HELPERS -----------------
        private static bool LooksLikeChannexId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();

            if (Guid.TryParse(s, out _)) return true;
            if (s.Length >= 20) return true;

            return false;
        }

        private static string ResolveChRoomTypeId(Dictionary<string, string> localCatToChRoomTypeId, string localCategoryId)
        {
            if (string.IsNullOrWhiteSpace(localCategoryId)) return null;

            if (localCatToChRoomTypeId != null &&
                localCatToChRoomTypeId.TryGetValue(localCategoryId.Trim(), out var rt) &&
                !string.IsNullOrWhiteSpace(rt))
            {
                return rt.Trim();
            }

            // if already channex id in datesrates.category_id
            return localCategoryId.Trim();
        }

        private static string ResolveChPlanId(
            Dictionary<string, string> planKeyToChPlanId,
            string localPlanId,
            string chRoomTypeId,
            string localCategoryId)
        {
            if (string.IsNullOrWhiteSpace(localPlanId)) return null;

            // already channex plan id
            if (LooksLikeChannexId(localPlanId))
                return localPlanId.Trim();

            // resolve via category_plan mapping using chRoomTypeId
            var key = MakePlanKey(localPlanId.Trim(), chRoomTypeId.Trim());
            if (planKeyToChPlanId != null &&
                planKeyToChPlanId.TryGetValue(key, out var pid) &&
                !string.IsNullOrWhiteSpace(pid))
                return pid.Trim();

            // fallback: if caller still passes localCategoryId and it is already chRoomTypeId
            if (!string.IsNullOrWhiteSpace(localCategoryId))
            {
                var key2 = MakePlanKey(localPlanId.Trim(), localCategoryId.Trim());
                if (planKeyToChPlanId != null &&
                    planKeyToChPlanId.TryGetValue(key2, out var pid2) &&
                    !string.IsNullOrWhiteSpace(pid2))
                    return pid2.Trim();
            }

            return null;
        }

        // ----------------- CHUNK -----------------
        private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int chunkSize)
        {
            if (items == null || items.Count == 0) yield break;
            if (chunkSize <= 0) chunkSize = 200;

            for (int i = 0; i < items.Count; i += chunkSize)
            {
                int take = Math.Min(chunkSize, items.Count - i);
                var batch = new List<T>(take);
                for (int j = 0; j < take; j++)
                    batch.Add(items[i + j]);
                yield return batch;
            }
        }
    }
}
