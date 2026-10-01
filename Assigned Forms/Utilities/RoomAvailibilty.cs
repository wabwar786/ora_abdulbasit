using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Web;

namespace hotelsoftware.Utilities
{
    public static class RoomAvailibilty
    {
        static   string connectionString = ConfigurationManager.ConnectionStrings["con"].ConnectionString;
        public static bool IsRoomAvailableForRange(string hotelId, string roomNo, string roomCategory, DateTime arr, DateTime dep, string excludeRegIdBase)
        {
            // excludeRegIdBase = regid without _R
            // roomCategory = category text (rt.room_category)

            const string sql = @"
-- 1) Block check (any overlap)
IF EXISTS (
    SELECT 1
    FROM RoomBlocksTB rb
    WHERE rb.HotelID = @hotelId
      AND rb.RoomNo  = @roomNo
      AND rb.IsActive = 1
     AND (
      CAST(rb.BlockStartDate AS DATE) < @dep
  AND  @arr < DATEADD(DAY, 1, CAST(ISNULL(rb.BlockEndDate,'9999-12-31') AS DATE)))
)
BEGIN
    SELECT 0; RETURN;
END

-- 2) Occupancy check (GuestInformationLogTB via payments)
IF EXISTS (
    SELECT 1
    FROM payments p
    INNER JOIN GuestInformationLogTB gi
        ON gi.reg_id = p.reg_id AND gi.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.room_no  = @roomNo
      AND p.descr    = 'Room Rent'
      AND p.res_status IN ('check in','reservation')
      AND ISNULL(p.reg_id,'') <> @excludeReg
      AND (CAST(p.ArrivalDate AS DATE) < @dep)
      AND (@arr < CAST(p.DepartureDate AS DATE))
)
BEGIN
    SELECT 0; RETURN;
END

-- 3) Occupancy check (NewReservationsTB via payments)
IF EXISTS (
    SELECT 1
    FROM payments p
    INNER JOIN NewReservationsTB nr
        ON nr.reg_id = p.reg_id AND nr.hotel_id = p.hotel_id
    WHERE p.hotel_id = @hotelId
      AND p.room_no  = @roomNo
      AND p.descr    = 'Room Rent'
      AND p.res_status IN ('check in','reservation')
      AND ISNULL(p.reg_id,'') <> @excludeReg
      AND (CAST(p.ArrivalDate AS DATE) < @dep)
      AND (@arr < CAST(p.DepartureDate AS DATE))
)
BEGIN
    SELECT 0; RETURN;
END

-- 4) Optional: ensure room belongs to the same category (safety)
IF EXISTS (
    SELECT 1
    FROM RoomsTB rt
    WHERE rt.Hotel_id = @hotelId
      AND rt.room_no  = @roomNo
      AND ISNULL(rt.room_category,'') = @roomCategory
)
BEGIN
    SELECT 1; RETURN;
END

SELECT 0;
";

            using (var con = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@hotelId", hotelId);
                cmd.Parameters.AddWithValue("@roomNo", roomNo ?? "");
                cmd.Parameters.AddWithValue("@roomCategory", roomCategory ?? "");
                cmd.Parameters.AddWithValue("@arr", arr.Date);
                cmd.Parameters.AddWithValue("@dep", dep.Date);
                cmd.Parameters.AddWithValue("@excludeReg", excludeRegIdBase ?? "");

                con.Open();
                var o = cmd.ExecuteScalar();
                return (o != null && o != DBNull.Value && Convert.ToInt32(o) == 1);
            }
        }
    }
}