using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using Newtonsoft.Json.Linq;

public class ChannexAvailabilityItem
{
    public string RoomTypeId { get; set; }
    public DateTime Date { get; set; }
    public int Availability { get; set; }
}

public class ChannexClient
{
    private readonly string _apiKey;
    private readonly string _baseUrl;

    /// <param name="apiKey">Your Channex API key (user-api-key)</param>
    /// <param name="baseUrl">Default: https://staging.channex.io</param>
    public ChannexClient(string apiKey, string baseUrl)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentNullException("apiKey");

        if (string.IsNullOrEmpty(baseUrl))
            throw new ArgumentNullException("baseUrl");

        _apiKey = apiKey;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public ChannexClient(string apiKey)
        : this(apiKey, "https://staging.channex.io")
    {
    }

    /// <summary>
    /// Get availability per room type for a date range.
    /// Synchronous method (for .NET 3.5 / VS2008).
    /// </summary>
    public List<ChannexAvailabilityItem> GetAvailability(
        string propertyId,
        DateTime dateFrom,
        DateTime dateTo)
    {
        if (string.IsNullOrEmpty(propertyId))
            throw new ArgumentException("propertyId is required", "propertyId");

        if (dateTo < dateFrom)
            throw new ArgumentException("dateTo must be >= dateFrom", "dateTo");

        string fromStr = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toStr = dateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string url = _baseUrl
                     + "/api/v1/availability"
                     + "?filter[date][gte]=" + fromStr
                     + "&filter[date][lte]=" + toStr
                     + "&filter[property_id]=" + propertyId;

        string json = DoGet(url);
        return ParseAvailability(json);
    }

    private string DoGet(string url)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json";
        request.Headers["user-api-key"] = _apiKey;

        using (var response = (HttpWebResponse)request.GetResponse())
        {
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                    return string.Empty;

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }

    private List<ChannexAvailabilityItem> ParseAvailability(string json)
    {
        var result = new List<ChannexAvailabilityItem>();

        if (string.IsNullOrEmpty(json))
            return result;

        JObject root;
        try
        {
            root = JObject.Parse(json);
        }
        catch
        {
            // Invalid JSON
            return result;
        }

        var data = root["data"] as JObject;
        if (data == null)
            return result;

        foreach (var roomTypeProp in data.Properties())
        {
            string roomTypeId = roomTypeProp.Name;
            var datesObject = roomTypeProp.Value as JObject;
            if (datesObject == null)
                continue;

            foreach (var dateProp in datesObject.Properties())
            {
                string dateStr = dateProp.Name;
                int availability = 0;

                int.TryParse(Convert.ToString(dateProp.Value), out availability);

                DateTime date;
                if (DateTime.TryParseExact(
                    dateStr,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
                {
                    var item = new ChannexAvailabilityItem();
                    item.RoomTypeId = roomTypeId;
                    item.Date = date;
                    item.Availability = availability;
                    result.Add(item);
                }
            }
        }

        return result;
    }
}
