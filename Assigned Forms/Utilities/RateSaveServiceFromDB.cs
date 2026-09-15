using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using static hotelsoftware.UpdateRateFromDB;

namespace hotelsoftware.Utilities
{
    public static class RateSaveServiceFromDB
    {
        private class ChildDef
        {
            public string CategoryId;
            public string ChildLocalPlanId;
            public string ParentPlanName;
            public string ChangeType;
            public decimal Percentage;
        }
        static bool IsValueType(string t)
        {
            t = (t ?? "").Trim().ToLowerInvariant();
            return t == "value" || t == "v" || t == "amount" || t == "fixed" || t == "flat";
        }

        static decimal ApplyChange(decimal baseValue, decimal amountOrPerc, string changeType)
        {
            if (IsValueType(changeType))
                return baseValue + amountOrPerc;

            // default: percentage
            return baseValue + (baseValue * (amountOrPerc / 100m));
        }

        // Helper: apply adjustment using changetype
        
        //        public static (DateTime minDate, DateTime maxDate, int rowCount) SaveRatesFastWithDerived(
        //       string connStr,
        //       string hotelId,
        //       string username,
        //       string systemName,
        //       string ip,
        //       PreviewRequest req,
        //       out HashSet<string> affectedPlanIds,
        //       out HashSet<string> affectedCategoryIds)
        //        {
        //            affectedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        //            affectedCategoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        //            if (req == null || req.plans == null || req.rooms == null || req.ranges == null)
        //                throw new Exception("Invalid request");

        //            var daysSet = new HashSet<int>(req.days ?? new List<int> { 0, 1, 2, 3, 4, 5, 6 });

        //            // Helper: normalize changetype


        //            using (var con = new SqlConnection(connStr))
        //            {
        //                con.Open();
        //                using (var tx = con.BeginTransaction())
        //                {
        //                    // parent plan name for deriving
        //                    var planNameByCatAndLocal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        //                    // children definitions indexed by (cat|parentPlanName)
        //                    var childrenByCatAndParentName = new Dictionary<string, List<ChildDef>>(StringComparer.OrdinalIgnoreCase);

        //                    // parent adjustment map indexed by (cat|localplanid) => (amountOrPerc, changeType)
        //                    var parentAdjMap = new Dictionary<string, (decimal amountOrPerc, string changeType)>(StringComparer.OrdinalIgnoreCase);

        //                    using (var cmd = new SqlCommand(@"
        //SELECT category_id, localplanid, planname, parent_planid, changetype, ISNULL(percentage,0) AS percentage
        //FROM dbo.category_plan
        //WHERE hotel_id=@hid;", con, tx))
        //                    {
        //                        cmd.Parameters.AddWithValue("@hid", hotelId);
        //                        using (var r = cmd.ExecuteReader())
        //                        {
        //                            while (r.Read())
        //                            {
        //                                string cat = Convert.ToString(r["category_id"]) ?? "";
        //                                string localPlan = Convert.ToString(r["localplanid"]) ?? "";
        //                                string planName = Convert.ToString(r["planname"]) ?? "";
        //                                string parentPlanName = Convert.ToString(r["parent_planid"]); // can be null
        //                                string changeType = Convert.ToString(r["changetype"]) ?? "Percentage";
        //                                decimal amountOrPerc = Convert.ToDecimal(r["percentage"]); // used as % or value

        //                                if (!string.IsNullOrWhiteSpace(cat) && !string.IsNullOrWhiteSpace(localPlan))
        //                                {
        //                                    planNameByCatAndLocal[$"{cat}|{localPlan}"] = planName;
        //                                    parentAdjMap[$"{cat}|{localPlan}"] = (amountOrPerc, changeType);
        //                                }

        //                                // child: if it has parent_planid it is derived from parent plan NAME
        //                                if (!string.IsNullOrWhiteSpace(parentPlanName) && !string.IsNullOrWhiteSpace(localPlan))
        //                                {
        //                                    var def = new ChildDef
        //                                    {
        //                                        CategoryId = cat,
        //                                        ChildLocalPlanId = localPlan,
        //                                        ParentPlanName = parentPlanName,
        //                                        ChangeType = changeType,
        //                                        Percentage = amountOrPerc // holds percent or value depending on changetype
        //                                    };

        //                                    var key = $"{cat}|{parentPlanName}";
        //                                    if (!childrenByCatAndParentName.TryGetValue(key, out var list))
        //                                    {
        //                                        list = new List<ChildDef>();
        //                                        childrenByCatAndParentName[key] = list;
        //                                    }
        //                                    list.Add(def);
        //                                }
        //                            }
        //                        }
        //                    }

        //                    // ✅ Use dictionary to avoid duplicates
        //                    // key = hotel|category|plan|date -> values(rate, baseRate)
        //                    var map = new Dictionary<string, (decimal rate, decimal baseRate)>(StringComparer.OrdinalIgnoreCase);

        //                    DateTime? minDate = null, maxDate = null;

        //                    foreach (var rg in req.ranges)
        //                    {
        //                        if (rg == null) continue;

        //                        if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        //                            continue;
        //                        if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        //                            continue;
        //                        if (end < start) continue;

        //                        if (!minDate.HasValue || start.Date < minDate.Value) minDate = start.Date;
        //                        if (!maxDate.HasValue || end.Date > maxDate.Value) maxDate = end.Date;

        //                        for (var dt = start.Date; dt <= end.Date; dt = dt.AddDays(1))
        //                        {
        //                            int dow = (int)dt.DayOfWeek;
        //                            if (!daysSet.Contains(dow)) continue;

        //                            foreach (var categoryId in req.rooms)
        //                            {
        //                                if (string.IsNullOrWhiteSpace(categoryId)) continue;

        //                                foreach (var parentLocalPlanId in req.plans)
        //                                {
        //                                    if (string.IsNullOrWhiteSpace(parentLocalPlanId)) continue;

        //                                    decimal baseRate = rg.baseRate;

        //                                    // ✅ Parent plan adjustment based on changetype
        //                                    var pKey = $"{categoryId}|{parentLocalPlanId}";
        //                                    decimal parentRate;
        //                                    if (parentAdjMap.TryGetValue(pKey, out var pAdj))
        //                                    {
        //                                        parentRate = ApplyChange(baseRate, pAdj.amountOrPerc, pAdj.changeType);
        //                                    }
        //                                    else
        //                                    {
        //                                        // default no adjustment
        //                                        parentRate = baseRate;
        //                                    }

        //                                    // parent row (dedup)
        //                                    Upsert(map, hotelId, categoryId, parentLocalPlanId, dt, parentRate, baseRate);
        //                                    affectedPlanIds.Add(parentLocalPlanId);
        //                                    affectedCategoryIds.Add(categoryId);

        //                                    // derived children (based on parent plan NAME)
        //                                    planNameByCatAndLocal.TryGetValue(pKey, out var parentPlanName);
        //                                    if (!string.IsNullOrWhiteSpace(parentPlanName))
        //                                    {
        //                                        var ck = $"{categoryId}|{parentPlanName}";
        //                                        if (childrenByCatAndParentName.TryGetValue(ck, out var children))
        //                                        {
        //                                            foreach (var ch in children)
        //                                            {
        //                                                decimal childBase = parentRate;
        //                                                // ✅ Child changetype support (Value or Percentage)
        //                                                decimal childRate = ApplyChange(childBase, ch.Percentage, ch.ChangeType);
        //                                                Upsert(map, hotelId, categoryId, ch.ChildLocalPlanId, dt, childRate, childBase);
        //                                                affectedPlanIds.Add(ch.ChildLocalPlanId);
        //                                                affectedCategoryIds.Add(categoryId);
        //                                            }
        //                                        }
        //                                    }
        //                                }
        //                            }
        //                        }
        //                    }

        //                    // build TVP once from map
        //                    var tvp = BuildBulkTable();
        //                    foreach (var kv in map)
        //                    {
        //                        var parts = kv.Key.Split('|');
        //                        string h = parts[0];
        //                        string c = parts[1];
        //                        string p = parts[2];
        //                        DateTime d = DateTime.ParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture);

        //                        AddRow(tvp, h, c, p, d, kv.Value.rate, kv.Value.baseRate, ip, systemName, username, "1");
        //                    }

        //                    using (var cmd = new SqlCommand("dbo.UpsertDateRatesBulk", con, tx))
        //                    {
        //                        cmd.CommandType = CommandType.StoredProcedure;
        //                        var prm = cmd.Parameters.AddWithValue("@Rows", tvp);
        //                        prm.SqlDbType = SqlDbType.Structured;
        //                        prm.TypeName = "dbo.DateRateBulkType";
        //                        cmd.ExecuteNonQuery();
        //                    }

        //                    tx.Commit();
        //                    return (minDate ?? DateTime.Today, maxDate ?? DateTime.Today, tvp.Rows.Count);
        //                }
        //            }
        //        }

        public static (DateTime minDate, DateTime maxDate, int rowCount) SaveRatesFastWithDerived(
    string connStr,
    string hotelId,
    string username,
    string systemName,
    string ip,
    PreviewRequest req,
    out HashSet<string> affectedPlanIds,
    out HashSet<string> affectedCategoryIds)
        {
            affectedPlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            affectedCategoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (req == null || req.plans == null || req.rooms == null || req.ranges == null)
                throw new Exception("Invalid request");

            var daysSet = new HashSet<int>(req.days ?? new List<int> { 0, 1, 2, 3, 4, 5, 6 });

            // ===== Helpers =====
           

            // Resolve one category's derived rates for one day
            // Inputs:
            //  - baseRate: the hotel base rate for the range/day
            //  - rootsLocalPlanIds: req.plans (these are "selected" root plans)
            //  - parentAdjMap: adjustment for each plan (cat|localplanid)
            //  - planNameByCatAndLocal: localplanid -> planname (for matching parent_planid which is stored as plan name)
            //  - childrenByCatAndParentName: (cat|parentPlanName) -> list of children defs
            // Output:
            //  - computedRates[localplanid] = rate
            Dictionary<string, decimal> ComputeAllRatesForCategory(
                string categoryId,
                decimal baseRate,
                IEnumerable<string> rootsLocalPlanIds,
                Dictionary<string, (decimal amountOrPerc, string changeType)> parentAdjMap,
                Dictionary<string, string> planNameByCatAndLocal,
                Dictionary<string, List<ChildDef>> childrenByCatAndParentName)
            {
                // We'll compute rates for all reachable plans from the roots.
                var computed = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                // queue items to process
                var q = new Queue<string>();

                // 1) compute roots from baseRate
                foreach (var rootLp in rootsLocalPlanIds)
                {
                    if (string.IsNullOrWhiteSpace(rootLp)) continue;

                    var k = $"{categoryId}|{rootLp}";
                    decimal rootRate;

                    if (parentAdjMap.TryGetValue(k, out var adj))
                        rootRate = ApplyChange(baseRate, adj.amountOrPerc, adj.changeType);
                    else
                        rootRate = baseRate;

                    if (!computed.ContainsKey(rootLp))
                    {
                        computed[rootLp] = rootRate;
                        q.Enqueue(rootLp);
                    }
                }

                // 2) BFS/Propagation to children (multi-level)
                // Parent->Children matching is by parent plan NAME (as in your table)
                while (q.Count > 0)
                {
                    var parentLocalPlanId = q.Dequeue();

                    var parentKey = $"{categoryId}|{parentLocalPlanId}";
                    if (!computed.TryGetValue(parentLocalPlanId, out var parentRate))
                        continue;

                    // parent plan name needed to find children
                    if (!planNameByCatAndLocal.TryGetValue(parentKey, out var parentPlanName))
                        continue;

                    if (string.IsNullOrWhiteSpace(parentPlanName))
                        continue;

                    var ck = $"{categoryId}|{parentPlanName}";
                    if (!childrenByCatAndParentName.TryGetValue(ck, out var children))
                        continue;
                    foreach (var ch in children)
                    {
                        if (string.IsNullOrWhiteSpace(ch.ChildLocalPlanId)) continue;
                        // child base is parentRate (as you want)
                        decimal childBase = parentRate;
                        decimal childRate = ApplyChange(childBase, ch.Percentage, ch.ChangeType);
                        // if already computed, keep latest (or same)
                        // (if you want to prevent overwriting, add a cycle-check)
                        bool isNew = !computed.ContainsKey(ch.ChildLocalPlanId);
                        computed[ch.ChildLocalPlanId] = childRate;

                        if (isNew)
                            q.Enqueue(ch.ChildLocalPlanId);
                        else
                        {
                            // If it existed, still enqueue because downstream children may need refresh
                            q.Enqueue(ch.ChildLocalPlanId);
                        }
                    }
                }
                return computed;
            }

            using (var con = new SqlConnection(connStr))
            {
                con.Open();
                using (var tx = con.BeginTransaction())
                {
                    // localplanid->planname per category
                    var planNameByCatAndLocal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    // children defs indexed by (cat|parentPlanName)
                    var childrenByCatAndParentName = new Dictionary<string, List<ChildDef>>(StringComparer.OrdinalIgnoreCase);
                    // adjustment map for any plan row (cat|localplanid) => (amountOrPerc, changeType)
                    var parentAdjMap = new Dictionary<string, (decimal amountOrPerc, string changeType)>(StringComparer.OrdinalIgnoreCase);
                    // Load category_plan once
                    using (var cmd = new SqlCommand(@"
SELECT category_id, localplanid, planname, parent_planid, changetype, ISNULL(percentage,0) AS percentage
FROM dbo.category_plan
WHERE hotel_id=@hid;", con, tx))
                    {
                        cmd.Parameters.AddWithValue("@hid", hotelId);
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string cat = Convert.ToString(r["category_id"]) ?? "";
                                string localPlan = Convert.ToString(r["localplanid"]) ?? "";
                                string planName = Convert.ToString(r["planname"]) ?? "";
                                string parentPlanName = Convert.ToString(r["parent_planid"]); // plan name of parent, can be null
                                string changeType = Convert.ToString(r["changetype"]) ?? "Percentage";
                                decimal amountOrPerc = Convert.ToDecimal(r["percentage"]);   // percent or value

                                if (!string.IsNullOrWhiteSpace(cat) && !string.IsNullOrWhiteSpace(localPlan))
                                {
                                    planNameByCatAndLocal[$"{cat}|{localPlan}"] = planName;
                                    parentAdjMap[$"{cat}|{localPlan}"] = (amountOrPerc, changeType);
                                }

                                // derived child definition: parent_planid stores parent PLAN NAME
                                if (!string.IsNullOrWhiteSpace(cat) &&
                                    !string.IsNullOrWhiteSpace(localPlan) &&
                                    !string.IsNullOrWhiteSpace(parentPlanName))
                                {
                                    var def = new ChildDef
                                    {
                                        CategoryId = cat,
                                        ChildLocalPlanId = localPlan,
                                        ParentPlanName = parentPlanName,
                                        ChangeType = changeType,
                                        Percentage = amountOrPerc
                                    };

                                    var key = $"{cat}|{parentPlanName}";
                                    if (!childrenByCatAndParentName.TryGetValue(key, out var list))
                                    {
                                        list = new List<ChildDef>();
                                        childrenByCatAndParentName[key] = list;
                                    }
                                    list.Add(def);
                                }
                            }
                        }
                    }
                    // ✅ Use dictionary to avoid duplicates
                    var map = new Dictionary<string, (decimal rate, decimal baseRate)>(StringComparer.OrdinalIgnoreCase);
                    DateTime? minDate = null, maxDate = null;
                    foreach (var rg in req.ranges)
                    {
                        if (rg == null) continue;

                        if (!DateTime.TryParseExact(rg.start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                            continue;
                        if (!DateTime.TryParseExact(rg.end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                            continue;
                        if (end < start) continue;

                        if (!minDate.HasValue || start.Date < minDate.Value) minDate = start.Date;
                        if (!maxDate.HasValue || end.Date > maxDate.Value) maxDate = end.Date;

                        for (var dt = start.Date; dt <= end.Date; dt = dt.AddDays(1))
                        {
                            int dow = (int)dt.DayOfWeek;
                            if (!daysSet.Contains(dow)) continue;

                            foreach (var categoryId in req.rooms)
                            {
                                if (string.IsNullOrWhiteSpace(categoryId)) continue;

                                decimal baseRate = rg.baseRate;

                                // ✅ Compute ALL rates (roots + derived chain) for this category+day
                                var computedRates = ComputeAllRatesForCategory(
                                    categoryId,
                                    baseRate,
                                    req.plans,              // roots selected in UI (RO Flexi etc.)
                                    parentAdjMap,
                                    planNameByCatAndLocal,
                                    childrenByCatAndParentName);

                                // ✅ Upsert everything computed
                                foreach (var kvp in computedRates)
                                {
                                    string localPlanId = kvp.Key;
                                    decimal rate = kvp.Value;
                                    // baseRate stored should be:
                                    // - for root plans: rg.baseRate
                                    // - for derived plans: their base should be their parent rate
                                    // Your existing TVP includes baseRate; if you want exact parentBase for derived,
                                    // we'd need to store extra. For now: store rg.baseRate for roots and rg.baseRate for all,
                                    // OR better: store "rate" minus adjustment? Not reliable.
                                    // We'll keep storing baseRate as rg.baseRate to match your current design.
                                    //Upsert(map, hotelId, categoryId, localPlanId, dt, rate, baseRate);
                                    affectedPlanIds.Add(localPlanId);
                                    affectedCategoryIds.Add(categoryId);
                                }
                            }
                        }
                    }
                   // build TVP once from map
                   var tvp = BuildBulkTable();
                    foreach (var kv in map)
                    {
                        var parts = kv.Key.Split('|');
                        string h = parts[0];
                        string c = parts[1];
                        string p = parts[2];
                        DateTime d = DateTime.ParseExact(parts[3], "yyyyMMdd", CultureInfo.InvariantCulture);
                        AddRow(tvp, h, c, p, d, kv.Value.rate, kv.Value.baseRate, ip, systemName, username, "1");
                    }
                    //using (var cmd = new SqlCommand("dbo.UpsertDateRatesBulk", con, tx))
                    //{
                    //    cmd.CommandType = CommandType.StoredProcedure;
                    //    var prm = cmd.Parameters.AddWithValue("@Rows", tvp);
                    //    prm.SqlDbType = SqlDbType.Structured;
                    //    prm.TypeName = "dbo.DateRateBulkType";
                    //    cmd.ExecuteNonQuery();
                    //}
                    tx.Commit();
                    return (minDate ?? DateTime.Today, maxDate ?? DateTime.Today, tvp.Rows.Count);
                }
            }
        }

        private static void Upsert(
            Dictionary<string, (decimal rate, decimal baseRate)> map,
            string hotelId, string categoryId, string planId, DateTime date,
            decimal rate, decimal baseRate)
        {
            // unique key by date
            string key = $"{hotelId}|{categoryId}|{planId}|{date:yyyyMMdd}";

            // "last wins" (latest processed overwrites)
            map[key] = (Math.Round(rate, 2), Math.Round(baseRate, 2));
        }

        private static DataTable BuildBulkTable()
        {
            var dt = new DataTable();
            dt.Columns.Add("hotel_id", typeof(string));
            dt.Columns.Add("category_id", typeof(string));
            dt.Columns.Add("planid", typeof(string));
            dt.Columns.Add("date", typeof(DateTime));
            dt.Columns.Add("rate", typeof(decimal));
            dt.Columns.Add("baserate", typeof(decimal));
            dt.Columns.Add("ip", typeof(string));
            dt.Columns.Add("systemName", typeof(string));
            dt.Columns.Add("username", typeof(string));
            dt.Columns.Add("upload", typeof(int));
            dt.Columns.Add("uploadfrom", typeof(string));
            return dt;
        }
        private static void AddRow(
            DataTable tvp,
            string hotelId,
            string categoryId,
            string planId,
            DateTime date,
            decimal rate,
            decimal baseRate,
            string ip,
            string systemName,
            string username,
            string uploadfrom)
        {
            var row = tvp.NewRow();
            row["hotel_id"] = hotelId ?? "";
            row["category_id"] = categoryId ?? "";
            row["planid"] = planId ?? "";
            row["date"] = date;
            row["rate"] = rate;
            row["baserate"] = baseRate;
            row["ip"] = ip ?? "";
            row["systemName"] = systemName ?? "";
            row["username"] = username ?? "";
            row["upload"] = 0;
            row["uploadfrom"] = uploadfrom ?? "1";
            tvp.Rows.Add(row);
        }
    }
}
