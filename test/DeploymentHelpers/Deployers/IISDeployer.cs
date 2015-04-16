﻿#if DNX451
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.Framework.Logging;
using Microsoft.Web.Administration;

namespace DeploymentHelpers
{
    /// <summary>
    /// Deployer for IIS (Both Helios and NativeModule).
    /// </summary>
    public class IISDeployer : ApplicationDeployer
    {
        private IISApplication _application;
        private CancellationTokenSource _hostShutdownToken = new CancellationTokenSource();

        public IISDeployer(DeploymentParameters startParameters, ILogger logger)
            : base(startParameters, logger)
        {
        }

        public override DeploymentResult Deploy()
        {
            // Start timer
            StartTimer();

            // Only supports publish and run on IIS.
            DeploymentParameters.PublishApplicationBeforeDeployment = true;

            _application = new IISApplication(DeploymentParameters, Logger);

            DeploymentParameters.DnxRuntime = PopulateChosenRuntimeInformation();

            // Publish to IIS root\application folder.
            DnuPublish(publishRoot: _application.WebSiteRootFolder);

            // Drop an ini file instead of setting environment variable.
            SetAspEnvironmentWithIni();

            // Setup the IIS Application.
            if (DeploymentParameters.ServerType == ServerType.IISNativeModule)
            {
                TurnRammFarOnNativeModule();
            }

            _application.Deploy();
            Logger.LogInformation("Successfully finished IIS application directory setup.");

            return new DeploymentResult
            {
                WebRootLocation = DeploymentParameters.ApplicationPath,
                DeploymentParameters = DeploymentParameters,
                // Accomodate the vdir name.
                ApplicationBaseUri = new UriBuilder(Uri.UriSchemeHttp, "localhost", _application.Port, _application.VirtualDirectoryName).Uri.AbsoluteUri + "/",
                HostShutdownToken = _hostShutdownToken.Token
            };
        }

        private void SetAspEnvironmentWithIni()
        {
            // Drop a Microsoft.AspNet.Hosting.ini with ASPNET_ENV information.
            Logger.LogInformation("Creating Microsoft.AspNet.Hosting.ini file with ASPNET_ENV.");
            var iniFile = Path.Combine(DeploymentParameters.ApplicationPath, "Microsoft.AspNet.Hosting.ini");
            File.WriteAllText(iniFile, string.Format("ASPNET_ENV={0}", DeploymentParameters.EnvironmentName));
        }

        private void TurnRammFarOnNativeModule()
        {
            Logger.LogInformation("Turning runAllManagedModulesForAllRequests=true in web.config for native module.");
            var webConfig = Path.Combine(DeploymentParameters.ApplicationPath, "web.config");
            var configuration = new XmlDocument();
            configuration.LoadXml(File.ReadAllText(webConfig));

            // https://github.com/aspnet/Helios/issues/77
            var rammfarAttribute = configuration.CreateAttribute("runAllManagedModulesForAllRequests");
            rammfarAttribute.Value = "true";
            var modulesNode = configuration.CreateElement("modules");
            modulesNode.Attributes.Append(rammfarAttribute);
            var systemWebServerNode = configuration.CreateElement("system.webServer");
            systemWebServerNode.AppendChild(modulesNode);
            configuration.SelectSingleNode("//configuration").AppendChild(systemWebServerNode);
            configuration.Save(webConfig);
        }

        public override void Dispose()
        {
            if (_application != null)
            {
                _application.StopAndDeleteAppPool();
                Logger.LogError("Application pool was shutdown successfully.");
                TriggerHostShutdown(_hostShutdownToken);
            }

            CleanPublishedOutput();
            InvokeUserApplicationCleanup();

            StopTimer();
        }

        private class IISApplication
        {
            private const string WEBSITE_NAME = "TestWebSite";
            private const string NATIVE_MODULE_MANAGED_RUNTIME_VERSION = "vCoreFX";

            private readonly ServerManager _serverManager = new ServerManager();
            private readonly DeploymentParameters _startParameters;
            private readonly ILogger _logger;
            private ApplicationPool _applicationPool;
            private Application _application;

            public string VirtualDirectoryName { get; set; }

            public string WebSiteRootFolder
            {
                get
                {
                    return Path.Combine(
                        Environment.GetEnvironmentVariable("SystemDrive") + @"\",
                        "inetpub",
                        "TestWebSite");
                }
            }

            public int Port
            {
                get
                {
                    return new Uri(_startParameters.ApplicationBaseUriHint).Port;
                }
            }

            public IISApplication(DeploymentParameters startParameters, ILogger logger)
            {
                _startParameters = startParameters;
                _logger = logger;
            }

            public void Deploy()
            {
                VirtualDirectoryName = new DirectoryInfo(_startParameters.ApplicationPath).Parent.Name;
                _applicationPool = CreateAppPool(VirtualDirectoryName);
                _application = Website.Applications.Add("/" + VirtualDirectoryName, _startParameters.ApplicationPath);
                _application.ApplicationPoolName = _applicationPool.Name;
                _serverManager.CommitChanges();
            }

            private Site _website;
            private Site Website
            {
                get
                {
                    _website = _serverManager.Sites.Where(s => s.Name == WEBSITE_NAME).FirstOrDefault();
                    if (_website == null)
                    {
                        _website = _serverManager.Sites.Add(WEBSITE_NAME, WebSiteRootFolder, Port);
                    }

                    return _website;
                }
            }

            private ApplicationPool CreateAppPool(string appPoolName)
            {
                var applicationPool = _serverManager.ApplicationPools.Add(appPoolName);
                if (_startParameters.ServerType == ServerType.IISNativeModule)
                {
                    // Not assigning a runtime version will choose v4.0 default.
                    applicationPool.ManagedRuntimeVersion = NATIVE_MODULE_MANAGED_RUNTIME_VERSION;
                }

                applicationPool.Enable32BitAppOnWin64 = (_startParameters.RuntimeArchitecture == RuntimeArchitecture.x86);
                _logger.LogInformation("Created {bit} application pool '{name}' with runtime version '{runtime}'.",
                    _startParameters.RuntimeArchitecture, applicationPool.Name,
                    applicationPool.ManagedRuntimeVersion ?? "default");
                return applicationPool;
            }

            public void StopAndDeleteAppPool()
            {
                if (_applicationPool != null)
                {
                    _logger.LogInformation("Stopping application pool '{name}' and deleting application.", _applicationPool.Name);
                    _applicationPool.Stop();
                }

                // Remove the application from website.
                if (_application != null)
                {
                    _application = Website.Applications.Where(a => a.Path == _application.Path).FirstOrDefault();
                    Website.Applications.Remove(_application);
                    _serverManager.ApplicationPools.Remove(_serverManager.ApplicationPools[_applicationPool.Name]);
                    _serverManager.CommitChanges();
                    _logger.LogInformation("Successfully stopped application pool '{name}' and deleted application from IIS.", _applicationPool.Name);
                }
            }
        }
    }
}
#endif