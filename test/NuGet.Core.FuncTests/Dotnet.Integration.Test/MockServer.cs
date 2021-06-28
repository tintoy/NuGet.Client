// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Test.Server;
using Test.Utility;

namespace Dotnet.Integration.Test
{
    /// <summary>
    /// A Mock Server that is used to mimic a NuGet Server.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable")]
    internal class MockServer : IDisposable
    {
        private Task _listenerTask;
        private bool _disposed = false;

        public string BasePath { get; }
        public HttpListener Listener { get; }
        private PortReserverOfMockServer PortReserver { get; }
        public RouteTable Get { get; }
        public RouteTable Put { get; }
        public RouteTable Delete { get; }
        public string Uri { get { return PortReserver.BaseUri; } }

        /// <summary>
        /// Observe requests without handling them directly.
        /// </summary>
        public Action<HttpListenerContext> RequestObserver { get; set; } = (x) => { };

        /// <summary>
        /// Initializes an instance of MockServer.
        /// </summary>
        public MockServer()
        {
            BasePath = $"/{Guid.NewGuid().ToString("D")}";

            PortReserver = new PortReserverOfMockServer(BasePath);

            // tests that cancel downloads and exit will cause the mock server to throw, this should be ignored.
            Listener = new HttpListener()
            {
                IgnoreWriteExceptions = true
            };

            Listener.Prefixes.Add(PortReserver.BaseUri);

            Get = new RouteTable(BasePath);
            Put = new RouteTable(BasePath);
            Delete = new RouteTable(BasePath);
        }

        private List<string> ServerWarnings { get; } = new List<string>();

        /// <summary>
        /// Starts the mock server.
        /// </summary>
        public void Start()
        {
            Listener.Start();
            _listenerTask = Task.Factory.StartNew(() => HandleRequest());
        }

        /// <summary>
        /// Stops the mock server.
        /// </summary>
        public void Stop()
        {
            try
            {
                Listener.Abort();

                var task = _listenerTask;
                _listenerTask = null;

                if (task != null)
                {
                    task.Wait();
                }
            }
            catch (Exception ex)
            {
                Debug.Fail(ex.ToString());
            }
        }

        /// <summary>
        /// Gets the absolute path of a URL minus the random base path.
        /// This enables tests to get the stable part of a request URL.
        /// </summary>
        /// <param name="request">An <see cref="HttpListenerRequest"/> instance.</param>
        /// <returns>The stable part of a request URL's absolute path.</returns>
        public string GetRequestUrlAbsolutePath(HttpListenerRequest request)
        {
            return request.Url.AbsolutePath.Substring(BasePath.Length);
        }

        /// <summary>
        /// Gets the path and query parts of a URL minus the random base path.
        /// This enables tests to get the stable part of a request URL.
        /// </summary>
        /// <param name="request">An <see cref="HttpListenerRequest"/> instance.</param>
        /// <returns>The stable part of a request URL's path and query.</returns>
        public string GetRequestUrlPathAndQuery(HttpListenerRequest request)
        {
            return request.Url.PathAndQuery.Substring(BasePath.Length);
        }

        /// <summary>
        /// Gets the raw URL minus the random base path.
        /// This enables tests to get the stable part of a request URL.
        /// </summary>
        /// <param name="request">An <see cref="HttpListenerRequest"/> instance.</param>
        /// <returns>The stable part of a request URL's raw URL.</returns>
        public string GetRequestRawUrl(HttpListenerRequest request)
        {
            return request.RawUrl.Substring(BasePath.Length);
        }

        /// <summary>
        /// Gets the pushed package from a nuget push request.
        /// </summary>
        /// <param name="r">The request generated by nuget push command.</param>
        /// <returns>The content of the package that is pushed.</returns>
        public static byte[] GetPushedPackage(HttpListenerRequest r)
        {
            byte[] buffer;
            using (var memoryStream = new MemoryStream())
            {
                r.InputStream.CopyTo(memoryStream);
                buffer = memoryStream.ToArray();
            }

            byte[] result = new byte[] { };
            var multipartContentType = "multipart/form-data; boundary=";
            if (!r.ContentType.StartsWith(multipartContentType, StringComparison.Ordinal))
            {
                return result;
            }
            var boundary = r.ContentType.Substring(multipartContentType.Length);
            byte[] delimiter = Encoding.UTF8.GetBytes("\r\n--" + boundary);
            int bodyStartIndex = Find(buffer, 0, new byte[] { 0x0d, 0x0a, 0x0d, 0x0a });
            if (bodyStartIndex == -1)
            {
                return result;
            }
            else
            {
                bodyStartIndex += 4;
            }

            int bodyEndIndex = Find(buffer, 0, delimiter);
            if (bodyEndIndex == -1)
            {
                //Patch, to deal with new binary format coming with the HttpClient
                //from dnxcore50. The right way should use existing libraries with
                //multi-part parsers
                byte[] delimiter2 = Encoding.UTF8.GetBytes("\r\n--");
                bodyEndIndex = Find(buffer, 0, delimiter2);
                if (bodyEndIndex == -1)
                {
                    return result;
                }
            }

            result = buffer.Skip(bodyStartIndex).Take(bodyEndIndex - bodyStartIndex).ToArray();
            return result;
        }

        public static void SavePushedPackage(HttpListenerRequest r, string outputFileName)
        {
            var buffer = GetPushedPackage(r);
            using (var of = new FileStream(outputFileName, FileMode.Create))
            {
                of.Write(buffer, 0, buffer.Length);
            }
        }

        /// <summary>
        /// Returns the index of the first occurrence of <paramref name="pattern"/> in
        /// <paramref name="buffer"/>. The search starts at a specified position.
        /// </summary>
        /// <param name="buffer">The buffer to search.</param>
        /// <param name="startIndex">The search start position.</param>
        /// <param name="pattern">The pattern to search.</param>
        /// <returns>The index position of <paramref name="pattern"/> if it is found in buffer, or -1
        /// if not.</returns>
        private static int Find(byte[] buffer, int startIndex, byte[] pattern)
        {
            for (int s = startIndex; s + pattern.Length <= buffer.Length; ++s)
            {
                if (StartsWith(buffer, s, pattern))
                {
                    return s;
                }
            }

            return -1;
        }

        /// <summary>
        /// Determines if the subset of <paramref name="buffer"/> starting at
        /// <paramref name="startIndex"/> starts with <paramref name="pattern"/>.
        /// </summary>
        /// <param name="buffer">The buffer to check.</param>
        /// <param name="startIndex">The start index of the subset to check.</param>
        /// <param name="pattern">The pattern to search.</param>
        /// <returns>True if the subset starts with the pattern; otherwise, false.</returns>
        private static bool StartsWith(byte[] buffer, int startIndex, byte[] pattern)
        {
            if (startIndex + pattern.Length > buffer.Length)
            {
                return false;
            }

            for (int i = 0; i < pattern.Length; ++i)
            {
                if (buffer[startIndex + i] != pattern[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static void SetResponseContent(HttpListenerResponse response, byte[] content)
        {
            // The client should not cache data between mock server calls
            response.AddHeader("Cache-Control", "no-cache, no-store");

            response.ContentLength64 = content.Length;

            try
            {
                response.OutputStream.Write(content, 0, content.Length);
            }
            catch (HttpListenerException)
            {
                // Listener exceptions may occur if the client drops the connection
            }
        }

        public static void SetResponseContent(HttpListenerResponse response, string text)
        {
            SetResponseContent(response, System.Text.Encoding.UTF8.GetBytes(text));
        }

        private void SetResponseNotFound(HttpListenerResponse response)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            SetResponseContent(response, "404 not found");
        }

        private void GenerateResponse(HttpListenerContext context)
        {
            var request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                RouteTable m = null;
                if (request.HttpMethod == "GET")
                {
                    m = Get;
                }
                else if (request.HttpMethod == "PUT")
                {
                    m = Put;
                }
                else if (request.HttpMethod == "DELETE")
                {
                    m = Delete;
                }

                if (m == null)
                {
                    SetResponseNotFound(response);
                }
                else
                {
                    var f = m.Match(request);
                    if (f != null)
                    {
                        var r = f(request);
                        if (r is string)
                        {
                            SetResponseContent(response, (string)r);
                        }
                        else if (r is Action<HttpListenerResponse>)
                        {
                            var action = (Action<HttpListenerResponse>)r;
                            action(response);
                        }
                        else if (r is Action<HttpListenerResponse, IPrincipal>)
                        {
                            var action = (Action<HttpListenerResponse, IPrincipal>)r;
                            action(response, context.User);
                        }
                        else if (r is int || r is HttpStatusCode)
                        {
                            response.StatusCode = (int)r;
                        }

                        foreach (var warning in ServerWarnings)
                        {
                            response.Headers.Add(ProtocolConstants.ServerWarningHeader, warning);
                        }
                    }
                    else
                    {
                        SetResponseNotFound(response);
                    }
                }
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private void HandleRequest()
        {
            while (true)
            {
                try
                {
                    var context = Listener.GetContext();

                    GenerateResponse(context);

                    RequestObserver(context);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (HttpListenerException ex)
                {
                    if (ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED ||
                        ex.ErrorCode == ErrorConstants.ERROR_INVALID_HANDLE ||
                        ex.ErrorCode == ErrorConstants.ERROR_INVALID_FUNCTION ||
                        RuntimeEnvironmentHelper.IsMono && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX ||
                        RuntimeEnvironmentHelper.IsLinux && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX ||
                        RuntimeEnvironmentHelper.IsMacOSX && ex.ErrorCode == ErrorConstants.ERROR_OPERATION_ABORTED_UNIX)
                    {
                        return;
                    }
                    else
                    {
                        System.Console.WriteLine("Unexpected error code: {0}. Ex: {1}", ex.ErrorCode, ex);
                        throw;
                    }
                }
            }
        }

        public void AddServerWarnings(string[] messages)
        {
            if (messages == null)
            {
                return;
            }

            foreach (var message in messages)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    ServerWarnings.Add(message);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // Closing the http listener
                Stop();

                // Disposing the PortReserver
                PortReserver.Dispose();

                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Represents the route table of the mock server.
    /// </summary>
    /// <remarks>
    /// The return type of a request handler could be:
    /// - string: the string will be sent back as the response content, and the response
    ///           status code is OK.
    /// - HttpStatusCode: the value is returned as the response status code.
    /// - Action&lt;HttpListenerResponse&gt;: The action will be called to construct the response.
    /// </remarks>
    public class RouteTable
    {
        private readonly string _basePath;
        private readonly List<Tuple<string, Func<HttpListenerRequest, object>>> _mappings;

        public RouteTable(string basePath)
        {
            _basePath = basePath ?? string.Empty;
            _mappings = new List<Tuple<string, Func<HttpListenerRequest, object>>>();
        }

        public void Add(string pattern, Func<HttpListenerRequest, object> f)
        {
            _mappings.Add(new Tuple<string, Func<HttpListenerRequest, object>>($"{_basePath}{pattern}", f));
        }

        public Func<HttpListenerRequest, object> Match(HttpListenerRequest r)
        {
            foreach (var m in _mappings)
            {
                if (r.Url.PathAndQuery.StartsWith(m.Item1, StringComparison.Ordinal))
                {
                    return m.Item2;
                }
            }

            return null;
        }
    }
}
