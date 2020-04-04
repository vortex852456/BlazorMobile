﻿using BlazorMobile.Common;
using BlazorMobile.Common.Helpers;
using BlazorMobile.Common.Interfaces;
using BlazorMobile.Common.Services;
using BlazorMobile.Consts;
using BlazorMobile.Controller;
using BlazorMobile.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unosquare.Labs.EmbedIO;
using Unosquare.Labs.EmbedIO.Constants;
using Unosquare.Labs.EmbedIO.Modules;
using Xamarin.Forms;

[assembly: InternalsVisibleTo("BlazorMobile.iOS")]
[assembly: InternalsVisibleTo("BlazorMobile.Android")]
[assembly: InternalsVisibleTo("BlazorMobile.UWP")]
[assembly: InternalsVisibleTo("BlazorMobile.Web")]
[assembly: InternalsVisibleTo("BlazorMobile.ElectronNET")]
namespace BlazorMobile.Services
{
    public static class WebApplicationFactory
    {
        private static WebServer server;
        private static CancellationTokenSource serverCts = new CancellationTokenSource();

        private static BlazorContextBridge blazorContextBridgeServer = null;

        private static bool _isStarted = false;

        private static bool IsStarted()
        {
            return _isStarted;
        }

        private static Func<Stream> _appResolver = null;

        private static void InvalidateCachedZipPackage()
        {
            if (_zipArchive != null)
            {
                try
                {
                    _zipArchive.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _zipArchive = null;
        }

         /// <summary>
        /// Reload the current application.
        /// 
        /// This option may be useful if you want to side-load another package
        /// of your own instead of the one shipped in your app by default.
        /// 
        /// If this is your requirement, you must update the delegate method used by <see cref="RegisterAppStreamResolver(Func{Stream})"/>
        /// pointing to your new package, before calling this method.
        /// 
        /// NOTE: This implementation will reload all <see cref="BlazorMobile.Components.IBlazorWebView"/> instances if you use more than one instance.
        /// </summary>
        public static void ReloadApplication()
        {
            NotifyBlazorAppReload();
        }

        /// <summary>
        /// Add the given Stream as a package in a data store on the device, with the given name
        /// </summary>
        /// <param name="name"></param>
        /// <param name="content"></param>
        public static bool AddPackage(string name, Stream content)
        {
            if (content == null)
            {
                throw new NullReferenceException($"{nameof(content)} cannot be null");
            }

            //TODO: Remove when ElectronNET is supported with a WASM behavior too
            if (BlazorDevice.IsElectronNET() && !BlazorDevice.IsUsingWASM())
            {
                throw new NotImplementedException("This feature is not implemented on ElectronNET with 'useWasm' option set to false");
            }

            //Force position to Begin of the Stream
            content.Seek(0, SeekOrigin.Begin);

            return GetApplicationStoreService().AddPackage(name, content);
        }

        /// <summary>
        /// Remove the package with the given name from the data store of the device
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static bool RemovePackage(string name)
        {
            //TODO: Remove when ElectronNET is supported with a WASM behavior too
            if (BlazorDevice.IsElectronNET() && !BlazorDevice.IsUsingWASM())
            {
                throw new NotImplementedException("This feature is not implemented on ElectronNET with 'useWasm' option set to false");
            }

            return GetApplicationStoreService().RemovePackage(name);
        }

        /// <summary>
        /// List available packages in the data store of the device
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<string> ListPackages()
        {
            //TODO: Remove when ElectronNET is supported with a WASM behavior too
            if (BlazorDevice.IsElectronNET() && !BlazorDevice.IsUsingWASM())
            {
                throw new NotImplementedException("This feature is not implemented on ElectronNET with 'useWasm' option set to false");
            }

            return GetApplicationStoreService().ListPackages();
        }

        /// <summary>
        /// Register how you get the Stream to your Blazor zipped application
        /// <para>For a performant unique entry point, getting a Stream from an</para>
        /// <para>Assembly manifest resource stream is recommended.</para>
        /// <para></para>
        /// <para>See <see cref="System.Reflection.Assembly.GetManifestResourceStream(string)"/></para>
        /// </summary>
        public static void RegisterAppStreamResolver(Func<Stream> resolver)
        {
            //If we are registering a new stream, we must invalidate previous ZipFile entry for package
            InvalidateCachedZipPackage();

            _appResolver = resolver;

            //Calling Init if not yet done. This will be a no-op if already called
            //We register init like this, as because of some linker problem with Xamarin,
            //we need to call the initializer from the "specialized project" (like Android)
            //that init itself and his components/renderer, then initializing this
            //
            //As iOS and UWP doesn't need a specific initialize at the moment but we may need
            //to call a generic init, the generic init is call in RegisterAppStream
            Init();
        }

        private static string _currentLoadedApplicationStorePackage = null;

        /// <summary>
        /// Return the current loaded application store package name.
        /// Return null if the package is not loaded from the store
        /// </summary>
        /// <returns></returns>
        internal static string GetCurrentLoadedApplicationStorePackageName()
        {
            return _currentLoadedApplicationStorePackage;
        }

        /// <summary>
        /// Load an app package with the given name, stored on the device.
        /// This is a shorthand on calling yourself <see cref="RegisterAppStreamResolver"/> and <see cref="ReloadApplication"/>
        /// as the loading is managed by all the entries you get through <see cref="AddPackage(string, Stream)"/>, <see cref="RemovePackage(string)"/>, <see cref="ListPackages"/>.
        /// </summary>
        /// <param name="name">The package to load</param>
        /// <returns></returns>
        public static bool LoadPackage(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new NullReferenceException($"{nameof(name)} cannot be null");
            }

            //TODO: Remove when ElectronNET is supported with a WASM behavior too
            if (BlazorDevice.IsElectronNET() && !BlazorDevice.IsUsingWASM())
            {
                throw new NotImplementedException("This feature is not implemented on ElectronNET when the 'useWasm' option is set to false on UseBlazorMobileWithElectronNET method");
            }

            var resolver = GetApplicationStoreService().GetPackageStreamResolver(name);
            if (resolver == null)
            {
                ConsoleHelper.WriteError($"{nameof(WebApplicationFactory)}.{nameof(LoadPackage)}: package '{name}' not found");
                return false;
            }

            _currentLoadedApplicationStorePackage = name;

            RegisterAppStreamResolver(resolver);
            ReloadApplication();

            return true;
        }

        private static Stream _appStream = null;

        /// <summary>
        /// Load an app package from the given Stream object. You are responsible for your Stream management.
        /// This mean that things may behave incorrectly if you close or dispose the stream. The given stream will
        /// be automatically disposed if you load or register another package instead.
        /// </summary>
        /// <param name="appStream">The package to load as a Stream</param>
        /// <returns></returns>
        public static bool LoadPackageFromStream(Stream appStream)
        {
            if (appStream == null)
            {
                throw new NullReferenceException($"{nameof(appStream)} cannot be null");
            }

            _appStream = appStream;

            Func<Stream> resolver = () => _appStream;

            if (resolver == null)
                return false;

            _currentLoadedApplicationStorePackage = null;

            RegisterAppStreamResolver(resolver);
            ReloadApplication();

            return true;
        }

        /// <summary>
        /// Load an app package with the given package assembly path if you have stored it the assembly as a static resource.
        /// This is the default mode used at start when shipping your base application from BlazorMobile template, but you can
        /// extend this in order to load different packages at your native app startup.
        /// </summary>
        /// <param name="packageAssembly">The assembly where your Blazor package is stored.
        /// TIPS: If you know a type stored in this assembly, you may resolve the assembly object with 'typeof(YourType).Assembly'</param>
        /// <param name="packagePath">The relative path where the Blazor package is stored in the assembly</param>
        /// <returns></returns>
        public static bool LoadPackageFromAssembly(Assembly packageAssembly, string packagePath)
        {
            if (packageAssembly == null)
            {
                throw new NullReferenceException($"{nameof(packageAssembly)} cannot be null");
            }

            if (string.IsNullOrEmpty(packagePath))
            {
                throw new NullReferenceException($"{nameof(packagePath)} cannot be null or empty");
            }

            Func<Stream> resolver = () => packageAssembly.GetManifestResourceStream($"{packageAssembly.GetName().Name}.{packagePath}");

            if (resolver == null)
                return false;

            Stream streamChecking = null;

            try
            {
                streamChecking = resolver.Invoke();
                if (streamChecking == null)
                    return false;
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteException(ex);
                if (streamChecking != null)
                {
                    streamChecking.Dispose();
                }

                return false;
            }

            streamChecking.Dispose();

            _currentLoadedApplicationStorePackage = null;

            RegisterAppStreamResolver(resolver);
            ReloadApplication();

            return true;
        }

        private static ZipArchive _zipArchive = null;
        private static object _zipLock = new object();
        private static ZipArchive GetZipArchive()
        {
            if (_zipArchive != null)
                return _zipArchive;

            //Stream is not disposed as it can be called in the future
            //Data source must be ideally loaded as a ressource like <see cref="Assembly.GetManifestResourceStream" /> for memory performance
            Stream dataSource = _appResolver();
            _zipArchive = new ZipArchive(dataSource, ZipArchiveMode.Read);

            return _zipArchive;
        }

        internal static MemoryStream GetResourceStream(string path)
        {
            if (_appResolver == null)
            {
                throw new NullReferenceException($"The Blazor package was not set! Please call {nameof(WebApplicationFactory)}.{nameof(WebApplicationFactory.RegisterAppStreamResolver)} method before launching your app");
            }

            //Specific use cases here !

            if (path.EndsWith(PlatformSpecific.BlazorWebAssemblyFileName) && PlatformSpecific.UseAlternateBlazorWASMScript())
            {
                return PlatformSpecific.GetAlternateBlazorWASMScript();
            }

            //End specific cases

            MemoryStream data = null;

            lock (_zipLock)
            {
                try
                {
                    ZipArchive archive = GetZipArchive();

                    //Data will contain the found file, outputed as a stream
                    data = new MemoryStream();
                    ZipArchiveEntry entry = archive.GetEntry(path);

                    if (entry != null)
                    {
                        Stream entryStream = entry.Open();
                        entryStream.CopyTo(data);
                        entryStream.Dispose();
                        data.Seek(0, SeekOrigin.Begin);
                    }
                    else
                    {
                        data?.Dispose();
                        data = null;
                    }
                }
                catch (Exception ex)
                {
                    ConsoleHelper.WriteException(ex);
                }
            }
            return data;
        }

        internal static byte[] GetResource(string path)
        {
            MemoryStream content = GetResourceStream(path);
            if (content == null)
                return null;
            byte[] resultSet = content?.ToArray();
            content?.Dispose();

            return resultSet;
        }

        internal static string GetContentType(string path)
        {
            if (path.EndsWith(".wasm"))
            {
                return "application/wasm";
            }
            if (path.EndsWith(".dll"))
            {
                return "application/octet-stream";
            }

            //No critical mimetypes to check
            return MimeTypes.GetMimeType(path);
        }

        internal static bool ResetBlazorViewIfHttpPortChanged()
        {
            bool needBlazorViewReload = false;

            if (IsStarted())
            {
                //Nothing to do
                return needBlazorViewReload;
            }

            int previousPort = GetHttpPort();

            //Try rebind on same port
            SetHttpPort(previousPort);

            int newPort = GetHttpPort();

            if (newPort != previousPort)
            {
                needBlazorViewReload = true;

                ConsoleHelper.WriteError("Unable to start web server on same previous port. Notifying BlazorWebView for reload...");

                //If port changed between affectation, that mean that an other process took the port.
                //We need to reload any Blazor webview instance
                NotifyBlazorAppReload();
            }

            return needBlazorViewReload;
        }

        internal static event EventHandler BlazorAppNeedReload;

        /// <summary>
        /// IBlazorWebView registered to this event must check the BlazorAppLaunched boolean.
        /// If the value is false, the WebView must reload
        /// </summary>
        internal static event EventHandler EnsureBlazorAppLaunchedOrReload;

        private static void NotifyBlazorAppReload()
        {
            Task.Run(async () =>
            {
                //Giving some time to new webserver with new port to load before raising event
                await Task.Delay(100);
                Device.BeginInvokeOnMainThread(() =>
                {
                    BlazorAppNeedReload?.Invoke(null, null);
                });
            });
        }

        internal static void NotifyEnsureBlazorAppLaunchedOrReload()
        {
            Task.Run(async () =>
            {
                //Giving some time to new webserver with new port to load before raising event
                await Task.Delay(100);
                Device.BeginInvokeOnMainThread(() =>
                {
                    EnsureBlazorAppLaunchedOrReload?.Invoke(null, null);
                });
            });
        }

        private const int DefaultHttpPort = 8888;

        private static int HttpPort = DefaultHttpPort;

        /// <summary>
        /// Define the HTTP port used for the webserver of your application.
        /// The HTTP port availability is not guaranted, so the port usage may be different at runtime.
        /// This method can be used for setting a fixed port during development for remote debugging functionnality
        /// Default is 8888.
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static void SetHttpPort(int port = DefaultHttpPort)
        {
            if (ContextHelper.IsElectronNET())
            {
                return;
            }

            //Try to bind user port first
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteException(ex);

                //Sanity check
                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                }

                //Trying to fallback on another port
                //The 0 port should return the first available port given by the OS
                listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                }
                catch (Exception)
                {
                    //Sanity check
                    try
                    {
                        listener.Stop();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            //The port given must be the one given in parameter and available, or another one firstly available

            HttpPort = port;
        }

        private static Func<string, string> _defaultPageDelegate;

        internal static Func<string, string> GetDefaultPageResultDelegate()
        {
            return _defaultPageDelegate;
        }

        public static void SetDefaultPageResult(Func<string, string> defaultPageDelegate)
        {
            _defaultPageDelegate = defaultPageDelegate;
        }

        public static int GetHttpPort()
        {
            return HttpPort;
        }

        private static string _localIP = null;
        internal static string GetLocalWebServerIP()
        {
            if (_localIP == null)
            {
                //Some Android platform does not have a universal loopback address
                //This is intended to detect the Loopback entry point
                //Thanks to https://github.com/unosquare/embedio/issues/207#issuecomment-433437410
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    listener.Start();
                    _localIP = ((IPEndPoint)listener.LocalEndpoint).Address.ToString();
                }
                catch
                {
                    _localIP = "localhost";
                }
                finally
                {
                    listener.Stop();
                }
            }

            return _localIP;
        }


        /// <summary>
        /// Return the current internal app base URI without the trailing slash. This value may change during lifetime if the registered listening port
        /// </summary>
        /// <returns></returns>
        public static string GetBaseURL()
        {
            var webPlatform = DependencyService.Get<IWebApplicationPlatform>();
            return webPlatform.GetBaseURL();
        }

        internal static string GetQueryPath(string path)
        {
            path = WebUtility.UrlDecode(path.TrimStart('/'));

            if (string.IsNullOrEmpty(path))
            {
                //We are calling the index page. We must call index.html but hide it from url for Blazor routing.
                path = Constants.DefaultPage;
            }

            return path;
        }

        internal static MemoryStream ManageIndexPageRendering(MemoryStream originalContent)
        {
            string indexContent = Encoding.UTF8.GetString(originalContent.ToArray());
            originalContent.Dispose();

            //Do user logic
            var userDelegate = GetDefaultPageResultDelegate();
            if (userDelegate != null)
            {
                indexContent = userDelegate(indexContent);
            }

            //We inject the local contextBridgeURI from the mobile app
            //If this value is not null or unedefined during mobile app runtime, that mean the app is shipped and not external
            //App must be tied to this URI and not the component generated one
            indexContent = indexContent.Replace("</body>", $"<script type=\"application/javascript\">window.blazorContextBridgeURI = '{GetContextBridgeURI()}';\r\n</script>\r\n</body>");

            return new MemoryStream(Encoding.UTF8.GetBytes(indexContent));
        }

        internal static class PlatformSpecific
        {
            private static bool _delayedStartPatch = false;

            internal static void EnableDelayedStartPatch(bool value)
            {
                _delayedStartPatch = value;
            }

            /// <summary>
            /// Used for iOS 13 fix until it's fixed by Apple
            /// </summary>
            /// <returns></returns>
            internal static bool UseAlternateBlazorWASMScript()
            {
                return _delayedStartPatch;
            }

            private const string JsFilesPath = "Interop.Javascript.";

            //If future change, only one place to modify
            internal const string BlazorWebAssemblyFileName = _alternateBlazorWasmScriptName;

            private const string _alternateBlazorWasmScriptName = "blazor.webassembly.js";

            internal static MemoryStream GetAlternateBlazorWASMScript()
            {
                var assembly = typeof(WebApplicationFactory).Assembly;

                //Assembly name and Assembly namespace differ in this project
                string JsNamespace = $"BlazorMobile.{JsFilesPath}";

                MemoryStream outputStream = new MemoryStream();
                using (var contentStream = assembly.GetManifestResourceStream($"{JsNamespace}{_alternateBlazorWasmScriptName}"))
                {
                    contentStream.Seek(0, SeekOrigin.Begin);
                    contentStream.CopyTo(outputStream);
                    outputStream.Seek(0, SeekOrigin.Begin);
                    return outputStream;
                }
            }
        }

        internal static async Task ManageRequest(IWebResponse response)
        {
            response.SetEncoding("UTF-8");

            string path = GetQueryPath(response.GetRequestedPath());

            var content = GetResourceStream(path);

            //Manage Index content
            if (path == Constants.DefaultPage)
            {
                content = ManageIndexPageRendering(content);
            }

            response.AddResponseHeader("Cache-Control", "no-cache");
            response.AddResponseHeader("Access-Control-Allow-Origin", GetBaseURL());

            if (content == null)
            {
                //Content not found
                response.SetStatutCode(404);
                response.SetReasonPhrase("Not found");
                response.SetMimeType("text/plain");
                return;
            }

            response.SetStatutCode(200);
            response.SetReasonPhrase("OK");
            response.SetMimeType(GetContentType(path));
            await response.SetDataAsync(content);
        }

        private static bool _firstCall = true;

        /// <summary>
        /// Init the WebApplicationFactory with the given app stream resolver.
        /// Shorthand for <see cref="WebApplicationFactory.RegisterAppStreamResolver" />
        /// </summary>
        /// <param name="appStreamResolver"></param>
        internal static void Init(Func<Stream> appStreamResolver)
        {
            Init();
            RegisterAppStreamResolver(appStreamResolver);
        }

        private static IApplicationStoreService _applicationStoreService;

        internal static IApplicationStoreService GetApplicationStoreService()
        {
            if (_applicationStoreService == null)
            {
                _applicationStoreService = DependencyService.Get<IApplicationStoreService>();
            }

            return _applicationStoreService;
        }

        internal static void Init()
        {
            if (_firstCall)
            {
                //Register IBlazorXamarinDeviceService for getting base metadata for Blazor
                DependencyService.Register<IBlazorXamarinDeviceService, BlazorXamarinDeviceService>();

                //We should always register this service at load, except for Electron that should register it in his custom Xamarin.Forms driver
                if (ContextHelper.IsBlazorMobile())
                {
                    DependencyService.Register<IWebApplicationPlatform, BlazorMobileWebApplicationPlatform>();
                }

                BlazorXamarinDeviceService.InitRuntimePlatform();

                _firstCall = false;
            }
        }

        /// <summary>
        /// As test for the moment. 10 seconds max
        /// </summary>
        private const int NativeSocketTimeout = 1000;

        private const string _contextBridgeRelativeURI = ContextBridgeHelper._contextBridgeRelativeURI;

        internal static string GetContextBridgeURI()
        {
            return GetBaseURL().Replace("http://", "ws://") + _contextBridgeRelativeURI;
        }

        internal static BlazorContextBridge GetBlazorContextBridgeServer()
        {
            return blazorContextBridgeServer;
        }

        internal static bool _debugFeatures = false;

        /// <summary>
        /// Add additionals features like debugging with Mozilla WebIDE for Android platform, allowing CORS request...
        /// </summary>
        public static void EnableDebugFeatures()
        {
            _debugFeatures = true;
        }

        internal static bool AreDebugFeaturesEnabled()
        {
            return _debugFeatures;
        }

        internal static void StartWebServer()
        {
            Init(); //No-op if called twice

            if (IsStarted())
            {
                //Already started
                ConsoleHelper.WriteError("BlazorMobile: Cannot start Webserver because it has already started");
                return;
            }

            WebServer server = null;

            if (AreDebugFeaturesEnabled())
            {
                //All wildcard url
                server = new WebServer(GetHttpPort(), RoutingStrategy.Regex);
            }
            else
            {
                //Restrict ip's to localhost
                server = new WebServer(GetBaseURL(), RoutingStrategy.Regex);
            }
            
            server.WithLocalSession();

            if (AreDebugFeaturesEnabled())
            {
                server.EnableCors();
            }

            server.RegisterModule(new WebApiModule());
            server.Module<WebApiModule>().RegisterController<BlazorController>();

            serverCts = new CancellationTokenSource();

            //Reference to the BlazorContextBridge Websocket service
            blazorContextBridgeServer = new BlazorContextBridge();
            blazorContextBridgeServer.CancellationToken = serverCts.Token;

            server.RegisterModule(new WebSocketsModule());
            server.Module<WebSocketsModule>().RegisterWebSocketsServer(_contextBridgeRelativeURI, blazorContextBridgeServer);

            Task.Factory.StartNew(async () =>
            {
                ConsoleHelper.WriteLine($"BlazorMobile: Starting server on port {GetHttpPort()}...");
        
                try
                {
                    _isStarted = true;
                    await server.RunAsync(serverCts.Token);
                }
                catch (InvalidOperationException e)
                {
                    _isStarted = false;

                    ConsoleHelper.WriteException(e);

                    //This call may be redundant with the previous case, but we must ensure that the StartWebServer invokation is done after clearing resources
                    ClearWebserverResources();

                    //If we are from the InvalidOperationException, the crash was not expected...Restarting webserver
                    Device.BeginInvokeOnMainThread(StartWebServer);
                }
                catch (OperationCanceledException e)
                {
                    _isStarted = false;

                    ConsoleHelper.WriteLine("BlazorMobile: Stopping server...");
                    ClearWebserverResources();
                }
            });
        }

        /// <summary>
        /// Clear Webserver resources and set it as not started
        /// </summary>
        private static void ClearWebserverResources()
        {
            try
            {
                if (blazorContextBridgeServer != null)
                {
                    blazorContextBridgeServer.Dispose();
                }
            }
            catch (Exception ex)
            {
            }

            try
            {
                server?.Dispose();
            }
            catch (Exception ex)
            {
            }

            blazorContextBridgeServer = null;
            server = null;
            _isStarted = false;
        }

        internal static void StopWebServer()
        {
            //In order to stop the waiting background thread
            try
            {
                //Will try to both stop Webserver and Socket server
                serverCts.Cancel();
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteException(ex);
                _isStarted = false;
            }
        }
    }
}
