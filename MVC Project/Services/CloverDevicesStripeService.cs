#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Orapmshms.Models;

namespace Orapmshms.Services;

/// <summary>
/// Late-bound Stripe.net.
///
/// Nothing here reimplements Stripe. Every call reaches the same class and the
/// same method the Web Forms page called, with the same options set on the same
/// properties; only the binding is done by name at run time, so this module
/// carries no hard reference to a package it does not ship.
///
/// Every lookup is cached in a static, so the reflection cost is paid once per
/// application, not once per request.
/// </summary>
public sealed class CloverDevicesStripeService : ICloverDevicesStripeService
{
    private const string NotAvailable =
        "Stripe.net was not found in this application, so Stripe could not be called.";

    private readonly ILogger<CloverDevicesStripeService> _logger;

    private static readonly object Gate = new();
    private static bool _probed;

    private static PropertyInfo? _apiKeyProperty;

    private static Type? _locationServiceType;
    private static Type? _locationCreateOptionsType;
    private static Type? _addressOptionsType;

    private static Type? _readerServiceType;
    private static Type? _readerCreateOptionsType;
    private static Type? _readerListOptionsType;

    public CloverDevicesStripeService(ILogger<CloverDevicesStripeService> logger)
    {
        _logger = logger;
        Probe();
    }

    public bool IsAvailable =>
        _apiKeyProperty != null && _readerServiceType != null;

    // ------------------------------------------------------------- discovery

    private static void Probe()
    {
        if (_probed) return;

        lock (Gate)
        {
            if (_probed) return;

            try
            {
                List<Type> types = new();

                // Stripe.dll is loaded on first use. Nothing in this module
                // references a Stripe type at compile time, so the assembly is
                // very likely not loaded yet - ask for it by name first.
                foreach (string name in new[] { "Stripe.net", "Stripe" })
                {
                    try
                    {
                        types.AddRange(Assembly.Load(new AssemblyName(name)).GetTypes());
                        break;
                    }
                    catch
                    {
                        // Not referenced under that name; try the next one.
                    }
                }

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;

                    try
                    {
                        types.AddRange(assembly.GetTypes());
                    }
                    catch
                    {
                        // A single unloadable assembly must not stop the probe.
                    }
                }

                Type? configuration = Find(types, "StripeConfiguration");

                _apiKeyProperty = configuration?.GetProperty(
                    "ApiKey",
                    BindingFlags.Public | BindingFlags.Static);

                _locationServiceType = Find(types, "LocationService");
                _locationCreateOptionsType = Find(types, "LocationCreateOptions");
                _addressOptionsType = Find(types, "AddressOptions");

                _readerServiceType = Find(types, "ReaderService");
                _readerCreateOptionsType = Find(types, "ReaderCreateOptions");
                _readerListOptionsType = Find(types, "ReaderListOptions");
            }
            catch
            {
                // Leave everything null; the caller degrades with a message.
            }

            // Latch only on success. If Stripe has not been loaded yet, the
            // next call probes again rather than failing for ever.
            _probed = _apiKeyProperty != null && _readerServiceType != null;
        }
    }

    /// <summary>
    /// Stripe's terminal types live under Stripe.Terminal; prefer those, so a
    /// same-named type elsewhere cannot win.
    /// </summary>
    private static Type? Find(List<Type> types, string name)
    {
        List<Type> matches = types
            .Where(t => string.Equals(t.Name, name, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return null;

        return matches.FirstOrDefault(t =>
                   t.Namespace != null &&
                   t.Namespace.StartsWith("Stripe.Terminal", StringComparison.Ordinal))
               ?? matches.FirstOrDefault(t =>
                   t.Namespace != null &&
                   t.Namespace.StartsWith("Stripe", StringComparison.Ordinal))
               ?? matches[0];
    }

    private static object NewInstance(Type type) =>
        Activator.CreateInstance(type)
        ?? throw new InvalidOperationException("Could not create " + type.Name + ".");

    private static void SetMember(object target, string name, object? value)
    {
        PropertyInfo? property = target.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance);

        if (property != null && property.CanWrite)
        {
            property.SetValue(target, value);
        }
    }

    private static string ReadString(object? source, params string[] names)
    {
        if (source == null) return string.Empty;

        Type type = source.GetType();

        foreach (string name in names)
        {
            PropertyInfo? property = type.GetProperty(
                name,
                BindingFlags.Public | BindingFlags.Instance);

            if (property != null && property.CanRead)
            {
                return Convert.ToString(property.GetValue(source)) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Stripe's service methods carry optional trailing parameters; supply
    /// Type.Missing for every one this module does not set.
    /// </summary>
    private static object? Call(object service, string name, params object?[] leading)
    {
        MethodInfo? method = service.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => string.Equals(m.Name, name, StringComparison.Ordinal))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault(m => m.GetParameters().Length >= leading.Length);

        if (method == null)
        {
            throw new InvalidOperationException(
                "Stripe's " + service.GetType().Name + "." + name + " was not found.");
        }

        ParameterInfo[] parameters = method.GetParameters();
        object?[] args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            args[i] = i < leading.Length ? leading[i] : Type.Missing;
        }

        return method.Invoke(service, args);
    }

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    // ------------------------------------------------------------------ calls

    public void SetApiKey(string apiKey)
    {
        if (_apiKeyProperty == null)
            throw new InvalidOperationException(NotAvailable);

        _apiKeyProperty.SetValue(null, apiKey);
    }

    public string CreateLocation(
        string displayName,
        string line1,
        string city,
        string state,
        string postalCode,
        string country)
    {
        if (_locationServiceType == null ||
            _locationCreateOptionsType == null ||
            _addressOptionsType == null)
        {
            throw new InvalidOperationException(NotAvailable);
        }

        try
        {
            object address = NewInstance(_addressOptionsType);
            SetMember(address, "Line1", line1);
            SetMember(address, "City", city);
            SetMember(address, "State", state);
            SetMember(address, "PostalCode", postalCode);
            SetMember(address, "Country", country);

            object options = NewInstance(_locationCreateOptionsType);
            SetMember(options, "DisplayName", displayName);
            SetMember(options, "Address", address);

            object service = NewInstance(_locationServiceType);
            object? location = Call(service, "Create", options);

            return ReadString(location, "Id");
        }
        catch (Exception ex)
        {
            throw Unwrap(ex);
        }
    }

    public List<CloverDevicesReaderInfo> ListReaders(string? locationId, int limit)
    {
        List<CloverDevicesReaderInfo> readers = new();

        if (_readerServiceType == null || _readerListOptionsType == null)
            throw new InvalidOperationException(NotAvailable);

        try
        {
            object options = NewInstance(_readerListOptionsType);
            SetMember(options, "Location", locationId);
            SetMember(options, "Limit", (long)limit);

            object service = NewInstance(_readerServiceType);
            object? list = Call(service, "List", options);

            if (list is IEnumerable items)
            {
                foreach (object? item in items)
                {
                    if (item == null) continue;
                    readers.Add(ToReader(item));
                }
            }

            return readers;
        }
        catch (Exception ex)
        {
            throw Unwrap(ex);
        }
    }

    public CloverDevicesReaderInfo CreateReader(
        string registrationCode,
        string label,
        string? locationId)
    {
        if (_readerServiceType == null || _readerCreateOptionsType == null)
            throw new InvalidOperationException(NotAvailable);

        try
        {
            object options = NewInstance(_readerCreateOptionsType);
            SetMember(options, "RegistrationCode", registrationCode);
            SetMember(options, "Label", label);
            SetMember(options, "Location", locationId);

            object service = NewInstance(_readerServiceType);
            object? reader = Call(service, "Create", options);

            return ToReader(reader);
        }
        catch (Exception ex)
        {
            throw Unwrap(ex);
        }
    }

    public CloverDevicesReaderInfo? GetReader(string readerId)
    {
        if (_readerServiceType == null) return null;

        try
        {
            object service = NewInstance(_readerServiceType);
            object? reader = Call(service, "Get", readerId);

            return reader == null ? null : ToReader(reader);
        }
        catch (Exception ex)
        {
            // Not found, or not accessible in this account - the page treated
            // both as "no".
            _logger.LogInformation(Unwrap(ex),
                "CloverDevices: Stripe does not have reader {ReaderId}.", readerId);

            return null;
        }
    }

    public void DeleteReader(string readerId)
    {
        if (_readerServiceType == null)
            throw new InvalidOperationException(NotAvailable);

        object service = NewInstance(_readerServiceType);

        try
        {
            Call(service, "Delete", readerId);
            return;
        }
        catch (Exception first)
        {
            Exception original = Unwrap(first);

            // Some accounts and reader types refuse a delete while the reader
            // is busy. Cancel any action, then delete again - the page's own
            // fallback.
            try
            {
                Call(service, "CancelAction", readerId);
                Call(service, "Delete", readerId);
                return;
            }
            catch
            {
                throw new InvalidOperationException(
                    "Stripe delete failed: " + original.Message, original);
            }
        }
    }

    private static CloverDevicesReaderInfo ToReader(object? source) => new()
    {
        Id = ReadString(source, "Id"),
        Label = ReadString(source, "Label"),
        DeviceType = ReadString(source, "DeviceType"),
        Status = ReadString(source, "Status")
    };
}
