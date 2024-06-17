namespace Configuration;

using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Reflection;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.Concurrent;

using VaultSharp;
using VaultSharp.Core;

using Vault;
using Logging;
using Threading.Extensions;

/// <summary>
/// Implementation for <see cref="BaseProvider"/> via Vault.
/// </summary>
public abstract class VaultProvider : EnvironmentProvider, IVaultProvider
{
    private static readonly TimeSpan _defaultRefreshIntervalConstant = TimeSpan.FromMinutes(10);
    private static TimeSpan _defaultRefreshInterval
    {
        get
        {
            if (!TimeSpan.TryParse(Environment.GetEnvironmentVariable("DEFAULT_PROVIDER_REFRESH_INTERVAL"), out var refreshInterval))
                return _defaultRefreshIntervalConstant;

            return refreshInterval;
        }
    }

    private static readonly HashSet<string> _propertyNamesIgnored = new()
    {
        nameof(Mount),
        nameof(Path),
    };

    private static readonly ConcurrentBag<VaultProvider> _providers = new();

    private IDictionary<string, object> _cachedValues = new Dictionary<string, object>();

    private static readonly Thread _refreshThread;
    private static readonly IVaultClient _client = VaultClientFactory.Singleton.GetClient();
    private static readonly ILogger _staticLogger = Logger.Singleton;

    /// <inheritdoc cref="IVaultProvider.Mount"/>
    public abstract string Mount { get; }

    /// <inheritdoc cref="IVaultProvider.Path"/>
    public abstract string Path { get; }

    /// <summary>
    /// Gets the refresh interval for the settings provider.
    /// </summary>
    private static TimeSpan RefreshInterval => _defaultRefreshInterval;

    /// <inheritdoc cref="INotifyPropertyChanged.PropertyChanged"/>
    public override event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Static constructor for <see cref="VaultProvider"/>
    /// </summary>
    static VaultProvider()
    {
        _refreshThread = new Thread(RefreshThread)
        {
            IsBackground = true,
            Name = "VaultProvider Refresh Thread"
        };

        _refreshThread.Start();

        _staticLogger?.Debug("VaultProvider: Started refresh thread!");
    }

    /// <inheritdoc cref="BaseProvider.SetRawValue{T}(string, T)"/>
    protected override void SetRawValue<T>(string variable, T value)
    {
        if (_client == null) return;

        _logger?.Debug("VaultProvider: Set value in vault at path '{0}/{1}/{2}'", Mount, Path, variable);

        var realValue = value.ToString();

        if (typeof(T).IsArray)
            realValue = string.Join(",", (value as Array).Cast<object>().ToArray());

        _cachedValues[variable] = realValue;

        ApplyCurrent();
    }

    /// <inheritdoc cref="IVaultProvider.ApplyCurrent"/>
    public void ApplyCurrent()
    {
        if (_client == null) return;

        // Build the current from the getters.
        var values = GetLatestValues();

        _logger?.Debug("VaultProvider: Writing secret '{0}/{1}' to Vault!", Mount, Path);

        _client?.V1.Secrets.KeyValue.V2.WriteSecretAsync(
            mountPoint: Mount,
            path: Path,
            data: values
        );
    }

    /// <summary>
    /// This boasts the ability to fetch the latest values from the getters.
    /// 
    /// This is because the cache does not cache default values, and we need to fetch the latest values from the getters.
    /// </summary>
    private Dictionary<string, object> GetLatestValues()
    {
        var getters = GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => !_propertyNamesIgnored.Contains(prop.Name));

        var newCachedValues = new Dictionary<string, object>();

        foreach (var getter in getters)
        {
            var getterName = getter.GetCustomAttribute<SettingNameAttribute>()?.Name ?? getter.Name;

            try
            {
                _logger?.Debug("VaultProvider: Fetching initial value for {0}.{1}", GetType().Name, getterName);

                var value = getter.GetGetMethod().Invoke(this, Array.Empty<object>());
                var realValue = value?.ToString() ?? string.Empty;

                if (value is Array arr)
                    realValue = string.Join(",", arr.Cast<object>().ToArray());

                newCachedValues.Add(getterName, realValue);
            }
            catch (TargetInvocationException ex)
            {
                _logger?.Debug("VaultProvider: Error occurred when fetching getter for '{0}.{1}': {2}", GetType().Name, getterName, ex.InnerException.Message);

                newCachedValues.Add(getterName, string.Empty);
            }
        }

        return newCachedValues;
    }

    /// <summary>
    /// Construct a new instance of <see cref="VaultProvider"/>
    /// </summary>
    /// <param name="logger">The <see cref="ILogger"/></param>
    protected VaultProvider(ILogger logger = null)
    {
        logger ??= Logger.Singleton;

        SetLogger(logger);

        _logger?.Debug("VaultProvider: Setup for '{0}/{1}' to refresh every '{2}' interval!", Mount, Path, RefreshInterval);

        _providers.Add(this);

        DoRefresh();
    }

    private static void RefreshThread()
    {
        while (true)
        {
            var providers = _providers.ToArray();

            foreach (var provider in providers)
            {
                try
                {
                    provider.DoRefresh();
                }
                catch (Exception ex)
                {
                    _staticLogger?.Error(ex);
                }
            }

            Thread.Sleep(RefreshInterval); // SetClient makes DoRefresh call.
        }
    }

    private void DoRefresh()
    {
        if (_client == null) return;

        _logger?.Debug("VaultProvider: DoRefresh for secret '{0}/{1}'", Mount, Path);

        try
        {
            var secret = _client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
                mountPoint: Mount,
                path: Path
            ).Sync();

            var values = secret.Data.Data;
            InvokePropertyChangedForChangedValues(values);

            lock (_cachedValues)
                _cachedValues = values;
        }
        catch (VaultApiException ex)
        {
            if (ex.HttpStatusCode == HttpStatusCode.NotFound)
            {
                _logger?.Debug("VaultProvider: DoRefresh for secret '{0}/{1}' failed: No secret found!", Mount, Path);

                return;
            }

            _logger?.Error(ex);

            throw;
        }
    }

    private bool HasProperty(ref string name)
    {
        var type = GetType();
        var getter = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);

        if (getter == null)
        {
            var propertyName = name;
            var property = (from prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                            let attrib = prop.GetCustomAttribute<SettingNameAttribute>()
                            where !_propertyNamesIgnored.Contains(prop.Name) && attrib?.Name == propertyName
                            select prop).SingleOrDefault();

            if (property == null)
            {
                _logger?.Debug("VaultProvider: Skipping property changed handler for '{0}' because settings provider '{1}' does not define it!", name, this);
                _logger?.Warning("{0}: Unknown property '{1}', make sure it is defined in the settings provider or has a appropriate [{2}] attribute!", GetType().Name, name, nameof(SettingNameAttribute));

                return false;
            }

            name = property.Name;
        }

        return true;
    }

    private void InvokePropertyChangedForChangedValues(IDictionary<string, object> newValues)
    {
        if (_cachedValues.Count == 0)
        {
            foreach (var kvp in newValues)
            {
                if (_propertyNamesIgnored.Contains(kvp.Key))
                {
                    _logger?.Debug("VaultProvider: Skipping property changed handler for '{0}' as it is a reserved property!", kvp.Key);

                    continue;
                }

                var propertyName = kvp.Key;
                if (!HasProperty(ref propertyName)) continue;

                _logger?.Debug("VaultProvider: Invoking property changed handler for '{0}'", propertyName);

                PropertyChanged?.Invoke(this, new(propertyName));
            }

            return;
        }

        foreach (var kvp in newValues)
        {
            if (_cachedValues.TryGetValue(kvp.Key, out var value))
                if (value.ToString().Equals(kvp.Value.ToString())) continue;

            if (_propertyNamesIgnored.Contains(kvp.Key))
            {
                _logger?.Debug("VaultProvider: Skipping property changed handler for '{0}' as it is a reserved property!", kvp.Key);

                continue;
            }

            var propertyName = kvp.Key;
            if (!HasProperty(ref propertyName)) continue;

            _logger?.Debug("Invoking property changed handler for '{0}'", propertyName);

            PropertyChanged?.Invoke(this, new(propertyName));
        }
    }

    /// <inheritdoc cref="IVaultProvider.Refresh"/>
    public void Refresh() => DoRefresh();

    /// <inheritdoc cref="BaseProvider.GetRawValue(string, out string)"/>
    protected override bool GetRawValue(string key, out string value)
    {
        object v;

        lock (_cachedValues)
            if (!_cachedValues.TryGetValue(key, out v))
                return base.GetRawValue(key, out value);

        if (v is JsonElement element)
            value = element.GetString();
        else
            value = v.ToString();

        return true;
    }
}