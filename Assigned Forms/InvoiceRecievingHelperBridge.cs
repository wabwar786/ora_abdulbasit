#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Forwards to the project's existing InvoiceHelper without referencing it at
/// compile time, so this module builds whether or not that class sits in the
/// same assembly, namespace or project it did under Web Forms.
///
/// Everything is resolved once and cached in static fields: after the first
/// request each call is a delegate invoke, not a lookup. Property readers for
/// the helper's own line/hotel/guest types are cached per type as compiled
/// getters, so mapping a 200-line invoice costs no repeated reflection.
///
/// The original page already read line values reflectively (GetPropertyValue),
/// so nothing here is looser than what it replaced.
/// </summary>
public sealed class InvoiceRecievingHelperBridge : IInvoiceRecievingHelper
{
    private const string HelperTypeName = "InvoiceHelper";

    private static readonly Lazy<Type?> HelperType = new(FindHelperType, isThreadSafe: true);

    private static readonly Lazy<MethodInfo?> MiGetHotel = Method("GetHotel", 1);
    private static readonly Lazy<MethodInfo?> MiGetGuest = Method("GetGuest", 2);
    private static readonly Lazy<MethodInfo?> MiGetPaymentLines = Method("GetPaymentLines", 2);
    private static readonly Lazy<MethodInfo?> MiGetLatestPaymentSummary = Method("GetLatestPaymentSummary", 2);
    private static readonly Lazy<MethodInfo?> MiBuildPayNowUrl = Method("BuildPayNowUrl", 8);
    private static readonly Lazy<MethodInfo?> MiGenerateQrDataUrl = Method("GenerateQrDataUrl", 1);

    private static readonly Dictionary<Type, Dictionary<string, Func<object, object?>>> GetterCache = new();
    private static readonly object GetterCacheLock = new();

    /// <summary>The stored tax-rate properties the page looked for, in its order.</summary>
    private static readonly string[] TaxRateProperties =
    {
        "VatPercent",
        "VATPercent",
        "VatRate",
        "VATRate",
        "TaxPercent",
        "TaxRate",
        "GstPercent",
        "GSTPercent",
        "GstRate",
        "GSTRate"
    };

    public static bool IsAvailable => HelperType.Value is not null;

    public InvoiceRecievingHotel GetHotel(string hotelId)
    {
        object? source = Invoke(MiGetHotel.Value, "GetHotel", hotelId);
        if (source is null) return new InvoiceRecievingHotel();

        var read = GettersFor(source.GetType());

        return new InvoiceRecievingHotel
        {
            Name = Str(read, source, "Name"),
            Address = Str(read, source, "Address"),
            Phone = Str(read, source, "Phone"),
            Email = Str(read, source, "Email"),
            WebsiteUrl = Str(read, source, "WebsiteUrl"),
            Logo = Str(read, source, "Logo"),
            CurrencySign = Str(read, source, "CurrencySign")
        };
    }

    public InvoiceRecievingGuest GetGuest(string hotelId, string regId)
    {
        object? source = Invoke(MiGetGuest.Value, "GetGuest", hotelId, regId);
        if (source is null) return new InvoiceRecievingGuest();

        var read = GettersFor(source.GetType());

        return new InvoiceRecievingGuest
        {
            FullName = Str(read, source, "FullName")
        };
    }

    public IReadOnlyList<InvoiceRecievingPaymentLine>? GetPaymentLines(string hotelId, string regId)
    {
        object? source = Invoke(MiGetPaymentLines.Value, "GetPaymentLines", hotelId, regId);

        // The page treated a null list as "no VAT column, no rows" - keep that.
        if (source is not IEnumerable sequence) return null;

        var mapped = new List<InvoiceRecievingPaymentLine>();
        Dictionary<string, Func<object, object?>>? read = null;

        foreach (object? item in sequence)
        {
            if (item is null) continue;

            read ??= GettersFor(item.GetType());

            mapped.Add(new InvoiceRecievingPaymentLine
            {
                Date = Date(read, item, "Date") ?? DateTime.MinValue,
                ArrivalDate = Date(read, item, "ArrivalDate"),
                DepartureDate = Date(read, item, "DepartureDate"),
                Descr = Str(read, item, "Descr"),
                Gross = Dec(read, item, "Gross"),
                Vat = Dec(read, item, "Vat"),
                StoredTaxRatePercent = StoredTaxRate(read, item)
            });
        }

        return mapped;
    }

    public InvoiceRecievingPaymentSummary? GetLatestPaymentSummary(string hotelId, string regId)
    {
        object? source = Invoke(MiGetLatestPaymentSummary.Value, "GetLatestPaymentSummary", hotelId, regId);
        if (source is null) return null;

        var read = GettersFor(source.GetType());

        return new InvoiceRecievingPaymentSummary
        {
            Remaining = Dec(read, source, "Remaining")
        };
    }

    public string BuildPayNowUrl(
        string hotelId,
        string regId,
        string fullName,
        decimal amount,
        string arrivalText,
        string departureText,
        string src,
        string extra)
    {
        object? url = Invoke(
            MiBuildPayNowUrl.Value,
            "BuildPayNowUrl",
            hotelId, regId, fullName, amount, arrivalText, departureText, src, extra);

        return Convert.ToString(url, CultureInfo.InvariantCulture) ?? "";
    }

    public string GenerateQrDataUrl(string payUrl)
    {
        object? dataUrl = Invoke(MiGenerateQrDataUrl.Value, "GenerateQrDataUrl", payUrl);
        return Convert.ToString(dataUrl, CultureInfo.InvariantCulture) ?? "";
    }

    // ---------------------------------------------------------------- lookup

    private static Lazy<MethodInfo?> Method(string name, int parameterCount) =>
        new(() =>
        {
            Type? type = HelperType.Value;
            if (type is null) return null;

            return type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == parameterCount);
        }, isThreadSafe: true);

    private static Type? FindHelperType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Skip the framework's own assemblies; the helper lives in app code.
            string? name = assembly.GetName().Name;
            if (name is null) continue;
            if (name.StartsWith("System", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft", StringComparison.Ordinal) ||
                name.StartsWith("netstandard", StringComparison.Ordinal)) continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Select(t => t!).ToArray();
            }
            catch
            {
                continue;
            }

            Type? match = types.FirstOrDefault(t =>
                string.Equals(t.Name, HelperTypeName, StringComparison.Ordinal));

            if (match is not null) return match;
        }

        return null;
    }

    private static object? Invoke(MethodInfo? method, string name, params object?[] args)
    {
        if (HelperType.Value is null)
            throw new InvalidOperationException(
                "InvoiceRecieving: the project's InvoiceHelper class was not found. " +
                "Register a class implementing IInvoiceRecievingHelper, or make InvoiceHelper " +
                "available to this assembly.");

        if (method is null)
            throw new InvalidOperationException(
                $"InvoiceRecieving: InvoiceHelper.{name} was not found with the expected number " +
                "of arguments. Implement IInvoiceRecievingHelper instead.");

        object? target = method.IsStatic ? null : Activator.CreateInstance(HelperType.Value);
        return method.Invoke(target, args);
    }

    // ------------------------------------------------------------- accessors

    private static Dictionary<string, Func<object, object?>> GettersFor(Type type)
    {
        lock (GetterCacheLock)
        {
            if (GetterCache.TryGetValue(type, out var cached)) return cached;

            var map = new Dictionary<string, Func<object, object?>>(StringComparer.OrdinalIgnoreCase);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                if (map.ContainsKey(property.Name)) continue;

                PropertyInfo captured = property;
                map[property.Name] = instance =>
                {
                    try { return captured.GetValue(instance, null); }
                    catch { return null; }
                };
            }

            GetterCache[type] = map;
            return map;
        }
    }

    private static object? Read(Dictionary<string, Func<object, object?>> read, object source, string name) =>
        read.TryGetValue(name, out var getter) ? getter(source) : null;

    private static string Str(Dictionary<string, Func<object, object?>> read, object source, string name) =>
        Convert.ToString(Read(read, source, name), CultureInfo.InvariantCulture) ?? "";

    private static decimal Dec(Dictionary<string, Func<object, object?>> read, object source, string name)
    {
        object? value = Read(read, source, name);
        if (value is null || value is DBNull) return 0m;
        if (value is decimal d) return d;

        return decimal.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : 0m;
    }

    private static DateTime? Date(Dictionary<string, Func<object, object?>> read, object source, string name)
    {
        object? value = Read(read, source, name);
        if (value is null || value is DBNull) return null;
        if (value is DateTime dt) return dt;

        return DateTime.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsed)
            ? parsed
            : null;
    }

    private static decimal StoredTaxRate(Dictionary<string, Func<object, object?>> read, object source)
    {
        foreach (string name in TaxRateProperties)
        {
            decimal rate = Dec(read, source, name);
            if (rate > 0m) return rate;
        }

        return 0m;
    }
}
