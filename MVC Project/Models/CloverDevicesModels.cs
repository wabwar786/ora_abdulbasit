#nullable enable

using System.Collections.Generic;

namespace Orapmshms.Models;

public sealed record CloverDevicesIntegrationStatus(string State, string Label, string Detail);

/// <summary>
/// Which modal the server decided to show, exactly as the Web Forms page
/// decided it with pnlReaderModal.Visible / pnlLocationModal.Visible.
/// </summary>
public enum CloverDevicesModal
{
    None = 0,
    Reader = 1,
    Location = 2
}

/// <summary>
/// The Stripe Devices page.
/// </summary>
public sealed class CloverDevicesViewModel
{
    public CloverDevicesIntegrationStatus StripeConnection { get; set; } =
        new("unknown", "Not checked", "Stripe connection has not been verified.");
    public CloverDevicesIntegrationStatus Tokenization { get; set; } =
        new("unknown", "Not checked", "Channex installation has not been verified.");
    public System.DateTimeOffset? StatusCheckedAt { get; set; }

    // An unknown API result must never be treated as a missing integration.
    public bool CanConnectStripe => StripeConnection.State == "negative" &&
        !string.IsNullOrWhiteSpace(StripeConnectUrl);
    public bool CanInstallTokenization => Tokenization.State == "negative";
    public bool HasRegisteredDevice => Devices.Count > 0;
    public bool CanRegisterDevice => CanConnectDevice &&
        StripeConnection.State == "positive" && !HasRegisteredDevice;

    /// <summary>The rows menurepeater was bound to.</summary>
    public List<CloverDevicesDeviceRow> Devices { get; set; } = new();

    /// <summary>hdnhotelid.</summary>
    public string HotelId { get; set; } = string.Empty;

    /// <summary>hf - the Stripe OAuth url the small window opens.</summary>
    public string StripeConnectUrl { get; set; } = string.Empty;

    /// <summary>
    /// btnConnectDevice.Enabled - false when HotelsSignUpTB.locid_stripe is
    /// empty, which is what Page_Load decided.
    /// </summary>
    public bool CanConnectDevice { get; set; }

    /// <summary>hdb_locid.</summary>
    public string LocationId { get; set; } = string.Empty;

    /// <summary>lblResult, plus every message the page used to alert().</summary>
    public string Message { get; set; } = string.Empty;

    public bool IsError { get; set; }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    /// <summary>Which modal is open on this render.</summary>
    public CloverDevicesModal Modal { get; set; } = CloverDevicesModal.None;

    // ---- reader modal (pnlReaderModal) ----

    /// <summary>hdnReaderid.</summary>
    public string ReaderId { get; set; } = string.Empty;

    /// <summary>txtReaderName.</summary>
    public string ReaderName { get; set; } = string.Empty;

    /// <summary>txtRegCode.</summary>
    public string RegistrationCode { get; set; } = string.Empty;

    /// <summary>regcodediv.Visible - hidden when the modal is in update mode.</summary>
    public bool ShowRegistrationCode { get; set; } = true;

    /// <summary>
    /// btnConnectReader vs btnUpdateReader: false is Connect Reader, true is
    /// Save Changes.
    /// </summary>
    public bool ReaderIsUpdate { get; set; }

    // ---- location modal (pnlLocationModal) ----

    public string LocationName { get; set; } = string.Empty;
    public string Address1 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostCode { get; set; } = string.Empty;

    /// <summary>txtCountry - the page shipped with GB in it.</summary>
    public string Country { get; set; } = "GB";

    public string EmptyText { get; set; } = "No devices connected.";
}

/// <summary>
/// One menurepeater row: the columns the Repeater bound by name.
/// </summary>
public sealed class CloverDevicesDeviceRow
{
    public string ReaderId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// A Stripe terminal reader as the Stripe SDK returned it, flattened so no
/// Stripe type crosses this module's boundary.
/// </summary>
public sealed class CloverDevicesReaderInfo
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// StripeSettingTB TOP(1), as GetLastStripeSetting() read it.
/// </summary>
public sealed class CloverDevicesStripeSetting
{
    public int Id { get; set; }
    public string? CallbackUrl { get; set; }
    public string? ClientId { get; set; }
    public string? StripeSecretKey { get; set; }
}

/// <summary>
/// What GetStripeCredentials() returned - a named type instead of a
/// ValueTuple, whose element names exist only at compile time.
/// </summary>
public sealed class CloverDevicesCredentials
{
    public string? StripeSecret { get; set; }
    public string? LocationId { get; set; }
}

/// <summary>
/// The outcome of one posted action: the message the page would have shown,
/// and the modal state it would have been left in.
/// </summary>
public sealed class CloverDevicesActionResult
{
    public string Message { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public CloverDevicesModal Modal { get; set; } = CloverDevicesModal.None;

    public string ReaderId { get; set; } = string.Empty;
    public string ReaderName { get; set; } = string.Empty;
    public bool ShowRegistrationCode { get; set; } = true;
    public bool ReaderIsUpdate { get; set; }
}
