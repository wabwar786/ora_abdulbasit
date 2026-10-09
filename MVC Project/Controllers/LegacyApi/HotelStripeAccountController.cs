using Microsoft.AspNetCore.Mvc;
using Orapmshms.Services.AvailabilityJobs;
using Orapmshms.Services.LegacyApi;
using Microsoft.Extensions.DependencyInjection;
using System;

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Authorization;

namespace Orapmshms.Controllers
{
    [Route("smartapi/api/hotel-stripe")]
    [AllowAnonymous]
    public class HotelStripeAccountController : LegacyApiControllerBase
    {
        private readonly string _connString =
            LegacyApiRuntime.ConnectionString;

        public class HotelStripeAccountResponse
        {
            public string HotelId { get; set; }
            public string AccessToken { get; set; }
            public string PublishableKey { get; set; }
        }

        [HttpGet]
        [Route("keys")]
        public IActionResult GetStripeKeysByHotelId(string hotelId)
        {
            if (string.IsNullOrWhiteSpace(hotelId))
                return BadRequest("hotelId is required.");

            try
            {
                using (SqlConnection conn = new SqlConnection(_connString))
                using (SqlCommand cmd = new SqlCommand(@"
                    SELECT TOP 1
                        HotelId,
                        AccessToken,
                        PublishableKey,
                        LiveMode,
                        AccessTokenExpiryDate
                    FROM dbo.HotelStripeAccounts
                    WHERE HotelId = @HotelId
                    ORDER BY CreatedAt DESC, Id DESC;
                ", conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.AddWithValue("@HotelId", hotelId.Trim());

                    conn.Open();

                    using (SqlDataReader rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read())
                            return NotFound();

                        var response = new HotelStripeAccountResponse
                        {
                            HotelId = rdr["HotelId"] == DBNull.Value ? "" : Convert.ToString(rdr["HotelId"]),
                            AccessToken = rdr["AccessToken"] == DBNull.Value ? "" : Convert.ToString(rdr["AccessToken"]),
                            PublishableKey = rdr["PublishableKey"] == DBNull.Value ? "" : Convert.ToString(rdr["PublishableKey"]),
                        };

                        return Ok(response);
                    }
                }
            }
            catch (Exception ex)
            {
                return InternalServerError(
                    new Exception("Error fetching Stripe keys: " + ex.Message, ex)
                );
            }
        }
    }
}