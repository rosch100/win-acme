﻿using Newtonsoft.Json;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Plugins.Base.Options;
using PKISharp.WACS.Plugins.TargetPlugins;
using PKISharp.WACS.Services.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PKISharp.WACS.Services
{
    internal class RenewalService : IRenewalStore
    {
        internal ISettingsService _settings;
        internal ILogService _log;
        internal IPluginService _plugin;
        internal PasswordGenerator _passwordGenerator;
        internal List<Renewal> _renewalsCache;

        public RenewalService(
            ISettingsService settings,
            ILogService log,
            PasswordGenerator password,
            IPluginService plugin)
        {
            _log = log;
            _plugin = plugin;
            _passwordGenerator = password;
            _settings = settings;
            _log.Debug("Renewal period: {RenewalDays} days", _settings.ScheduledTask.RenewalDays);
        }

        public IEnumerable<Renewal> FindByArguments(string id, string friendlyName)
        {
            // AND filtering by input parameters
            var ret = Renewals;
            if (!string.IsNullOrEmpty(friendlyName))
            {
                ret = ret.Where(x => string.Equals(friendlyName, x.LastFriendlyName, StringComparison.CurrentCultureIgnoreCase));
            }
            if (!string.IsNullOrEmpty(id))
            {
                ret = ret.Where(x => string.Equals(id, x.Id, StringComparison.CurrentCultureIgnoreCase));
            }
            return ret;
        }

        public void Save(Renewal renewal, RenewResult result)
        {
            var renewals = Renewals.ToList();
            if (renewal.New)
            {
                renewal.History = new List<RenewResult>();
                renewals.Add(renewal);
                _log.Information(LogType.All, "Adding renewal for {friendlyName}", renewal.LastFriendlyName);

            }

            // Set next date
            renewal.History.Add(result);
            if (result.Success)
            {
                _log.Information(LogType.All, "Next renewal scheduled at {date}", renewal.GetDueDate());
            }
            renewal.Updated = true;
            Renewals = renewals;
        }

        public void Import(Renewal renewal)
        {
            var renewals = Renewals.ToList();
            renewals.Add(renewal);
            _log.Information(LogType.All, "Importing renewal for {friendlyName}", renewal.LastFriendlyName);
            Renewals = renewals;
        }

        public void Encrypt()
        {
            var renewals = Renewals.ToList();
            foreach (var r in renewals)
            {
                r.Updated = true;
                _log.Information("Re-writing password information for {friendlyName}", r.LastFriendlyName);
            }
            WriteRenewals(renewals);
        }

        public IEnumerable<Renewal> Renewals
        {
            get => ReadRenewals();
            private set => WriteRenewals(value);
        }

        /// <summary>
        /// Cancel specific renewal
        /// </summary>
        /// <param name="renewal"></param>
        public void Cancel(Renewal renewal)
        {
            renewal.Deleted = true;
            Renewals = Renewals;
            _log.Warning("Renewal {target} cancelled", renewal);
        }

        /// <summary>
        /// Cancel everything
        /// </summary>
        public void Clear()
        {
            Renewals.All(x => x.Deleted = true);
            Renewals = Renewals;
            _log.Warning("All renewals cancelled");
        }

        /// <summary>
        /// Parse renewals from store
        /// </summary>
        public IEnumerable<Renewal> ReadRenewals()
        {
            if (_renewalsCache == null)
            {
                var list = new List<Renewal>();
                var di = new DirectoryInfo(_settings.Client.ConfigurationPath);
                var postFix = ".renewal.json";
                foreach (var rj in di.GetFiles($"*{postFix}", SearchOption.AllDirectories))
                {
                    try
                    {
                        var storeConverter = new PluginOptionsConverter<StorePluginOptions>(_plugin.PluginOptionTypes<StorePluginOptions>(), _log);
                        var result = JsonConvert.DeserializeObject<Renewal>(
                            File.ReadAllText(rj.FullName),
                            new ProtectedStringConverter(_log, _settings),
                            new StorePluginOptionsConverter(storeConverter),
                            new PluginOptionsConverter<TargetPluginOptions>(_plugin.PluginOptionTypes<TargetPluginOptions>(), _log),
                            new PluginOptionsConverter<CsrPluginOptions>(_plugin.PluginOptionTypes<CsrPluginOptions>(), _log),
                            storeConverter,
                            new PluginOptionsConverter<ValidationPluginOptions>(_plugin.PluginOptionTypes<ValidationPluginOptions>(), _log),
                            new PluginOptionsConverter<InstallationPluginOptions>(_plugin.PluginOptionTypes<InstallationPluginOptions>(), _log));
                        if (result == null)
                        {
                            throw new Exception("result is empty");
                        }
                        if (result.Id != rj.Name.Replace(postFix, ""))
                        {
                            throw new Exception($"mismatch between filename and id {result.Id}");
                        }
                        if (result.TargetPluginOptions == null)
                        {
                            throw new Exception("missing TargetPluginOptions");
                        }
                        if (result.ValidationPluginOptions == null)
                        {
                            throw new Exception("missing ValidationPluginOptions");
                        }
                        if (result.StorePluginOptions == null)
                        {
                            throw new Exception("missing StorePluginOptions");
                        }
                        if (result.CsrPluginOptions == null && result.TargetPluginOptions.Name != CsrOptions.NameLabel)
                        {
                            throw new Exception("missing CsrPluginOptions");
                        }
                        if (result.InstallationPluginOptions == null)
                        {
                            throw new Exception("missing InstallationPluginOptions");
                        }
                        if (string.IsNullOrEmpty(result.LastFriendlyName))
                        {
                            result.LastFriendlyName = result.FriendlyName;
                        }
                        if (result.History == null)
                        {
                            result.History = new List<RenewResult>();
                        }
                        result.RenewalDays = _settings.ScheduledTask.RenewalDays;
                        list.Add(result);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("Unable to read renewal {renewal}: {reason}", rj.Name, ex.Message);
                    }
                }
                _renewalsCache = list.OrderBy(x => x.GetDueDate()).ToList();
            }
            return _renewalsCache;
        }

        /// <summary>
        /// Serialize renewal information to store
        /// </summary>
        /// <param name="BaseUri"></param>
        /// <param name="Renewals"></param>
        public void WriteRenewals(IEnumerable<Renewal> Renewals)
        {
            var list = Renewals.ToList();
            list.ForEach(renewal =>
            {
                if (renewal.Deleted)
                {
                    var file = RenewalFile(renewal, _settings.Client.ConfigurationPath);
                    if (file != null && file.Exists)
                    {
                        file.Delete();
                    }
                }
                else if (renewal.Updated || renewal.New)
                {
                    var file = RenewalFile(renewal, _settings.Client.ConfigurationPath);
                    if (file != null)
                    {
                        File.WriteAllText(file.FullName, JsonConvert.SerializeObject(renewal, new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            Formatting = Formatting.Indented,
                            Converters = { new ProtectedStringConverter(_log, _settings) }
                        }));
                    }
                    renewal.New = false;
                    renewal.Updated = false;
                }
            });
            _renewalsCache = list.Where(x => !x.Deleted).OrderBy(x => x.GetDueDate()).ToList();
        }

        /// <summary>
        /// Determine location and name of the history file
        /// </summary>
        /// <param name="renewal"></param>
        /// <param name="configPath"></param>
        /// <returns></returns>
        private FileInfo RenewalFile(Renewal renewal, string configPath) => new FileInfo(Path.Combine(configPath, $"{renewal.Id}.renewal.json"));
    }


}