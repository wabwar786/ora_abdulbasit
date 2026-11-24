#nullable enable

using System.Collections.Generic;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// The Stripe.net calls the Web Forms page made, reached without a
/// compile-time reference to the package.
///
/// CloverDevices.aspx.cs used Stripe.StripeConfiguration,
/// Stripe.Terminal.LocationService and Stripe.Terminal.ReaderService directly.
/// Binding to them by name at run time means this module compiles and merges
/// whether or not Stripe.net is referenced by the MVC project - and the day it
/// is, every call goes straight to it.
/// </summary>
public interface ICloverDevicesStripeService
{
    /// <summary>True when Stripe.net was found and can be called.</summary>
    bool IsAvailable { get; }

    /// <summary>StripeConfiguration.ApiKey = apiKey</summary>
    void SetApiKey(string apiKey);

    /// <summary>
    /// new LocationService().Create(new LocationCreateOptions { ... }) - returns
    /// the new location's id.
    /// </summary>
    string CreateLocation(
        string displayName,
        string line1,
        string city,
        string state,
        string postalCode,
        string country);

    /// <summary>
    /// new ReaderService().List(new ReaderListOptions { Location = ..., Limit = 100 })
    /// </summary>
    List<CloverDevicesReaderInfo> ListReaders(string? locationId, int limit);

    /// <summary>
    /// new ReaderService().Create(new ReaderCreateOptions { RegistrationCode, Label, Location })
    /// </summary>
    CloverDevicesReaderInfo CreateReader(
        string registrationCode,
        string label,
        string? locationId);

    /// <summary>new ReaderService().Get(readerId) - null when Stripe does not have it.</summary>
    CloverDevicesReaderInfo? GetReader(string readerId);

    /// <summary>
    /// new ReaderService().Delete(readerId), falling back to CancelAction then
    /// Delete - the page's own two-step.
    /// </summary>
    void DeleteReader(string readerId);
}
