// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Threading.Tasks;
using System.Threading;

namespace System.Net
{
    public sealed unsafe partial class HttpListenerContext
    {
        private HttpListener _listener;
        private HttpListenerRequest _request;
        private HttpListenerResponse _response;
        private IPrincipal _user;
        private string _mutualAuthentication;
        private AuthenticationSchemes _authenticationSchemes;
        private ExtendedProtectionPolicy _extendedProtectionPolicy;

        internal const string NTLM = "NTLM";

        internal HttpListenerContext(HttpListener httpListener, RequestContextBase memoryBlob)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"httpListener {httpListener} requestBlob={((IntPtr)memoryBlob.RequestBlob)}");
            _listener = httpListener;
            _request = new HttpListenerRequest(this, memoryBlob);
            _authenticationSchemes = httpListener.AuthenticationSchemes;
            _extendedProtectionPolicy = httpListener.ExtendedProtectionPolicy;
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"HttpListener: {_listener} HttpListenerRequest: {_request}");
        }

        // Call this right after construction, and only once!  Not after it's been handed to a user.
        internal void SetIdentity(IPrincipal principal, string mutualAuthentication)
        {
            _mutualAuthentication = mutualAuthentication;
            _user = principal;
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"mutual: {(mutualAuthentication == null ? "<null>" : mutualAuthentication)}, Principal: {principal}");
        }

        public HttpListenerRequest Request
        {
            get
            {
                return _request;
            }
        }

        public HttpListenerResponse Response
        {
            get
            {
                if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
                if (_response == null)
                {
                    _response = new HttpListenerResponse(this);
                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"HttpListener: {_listener}, HttpListenerRequest: {_request}, HttpListenerResponse: {_response}");
                }
                if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
                return _response;
            }
        }

        public IPrincipal User => _user;

        // This can be used to cache the results of HttpListener.AuthenticationSchemeSelectorDelegate.
        internal AuthenticationSchemes AuthenticationSchemes
        {
            get
            {
                return _authenticationSchemes;
            }
            set
            {
                _authenticationSchemes = value;
            }
        }

        // This can be used to cache the results of HttpListener.ExtendedProtectionSelectorDelegate.
        internal ExtendedProtectionPolicy ExtendedProtectionPolicy
        {
            get
            {
                return _extendedProtectionPolicy;
            }
            set
            {
                _extendedProtectionPolicy = value;
            }
        }

        internal string MutualAuthentication
        {
            get
            {
                return _mutualAuthentication;
            }
        }

        internal HttpListener Listener
        {
            get
            {
                return _listener;
            }
        }

        internal SafeHandle RequestQueueHandle
        {
            get
            {
                return _listener.RequestQueueHandle;
            }
        }

        internal ThreadPoolBoundHandle RequestQueueBoundHandle
        {
            get
            {
                return _listener.RequestQueueBoundHandle;
            }
        }

        internal ulong RequestId
        {
            get
            {
                return Request.RequestId;
            }
        }

        public Task<HttpListenerWebSocketContext> AcceptWebSocketAsync(string subProtocol)
        {
            return this.AcceptWebSocketAsync(subProtocol,
                WebSocketHelpers.DefaultReceiveBufferSize,
                WebSocket.DefaultKeepAliveInterval);
        }

        public Task<HttpListenerWebSocketContext> AcceptWebSocketAsync(string subProtocol, TimeSpan keepAliveInterval)
        {
            return this.AcceptWebSocketAsync(subProtocol,
                WebSocketHelpers.DefaultReceiveBufferSize,
                keepAliveInterval);
        }

        public Task<HttpListenerWebSocketContext> AcceptWebSocketAsync(string subProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval)
        {
            WebSocketHelpers.ValidateOptions(subProtocol, receiveBufferSize, WebSocketBuffer.MinSendBufferSize, keepAliveInterval);

            ArraySegment<byte> internalBuffer = WebSocketBuffer.CreateInternalBufferArraySegment(receiveBufferSize, WebSocketBuffer.MinSendBufferSize, true);
            return this.AcceptWebSocketAsync(subProtocol,
                receiveBufferSize,
                keepAliveInterval,
                internalBuffer);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public Task<HttpListenerWebSocketContext> AcceptWebSocketAsync(string subProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval,
            ArraySegment<byte> internalBuffer)
        {
            return WebSocketHelpers.AcceptWebSocketAsync(this,
                subProtocol,
                receiveBufferSize,
                keepAliveInterval,
                internalBuffer);
        }

        internal void Close()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            try
            {
                if (_response != null)
                {
                    _response.Close();
                }
            }
            finally
            {
                try
                {
                    _request.Close();
                }
                finally
                {
                    IDisposable user = _user == null ? null : _user.Identity as IDisposable;

                    // For unsafe connection ntlm auth we dont dispose this identity as yet since its cached
                    if ((user != null) &&
                        (_user.Identity.AuthenticationType != NTLM) &&
                        (!_listener.UnsafeConnectionNtlmAuthentication))
                    {
                        user.Dispose();
                    }
                }
            }
            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        internal void Abort()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            ForceCancelRequest(RequestQueueHandle, _request.RequestId);
            try
            {
                _request.Close();
            }
            finally
            {
                IDisposable user = _user == null ? null : _user.Identity as IDisposable;
                if (user != null)
                {
                    user.Dispose();
                }
            }
            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        internal Interop.HttpApi.HTTP_VERB GetKnownMethod()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"Visited {nameof(GetKnownMethod)}()");
            return Interop.HttpApi.GetKnownVerb(Request.RequestBuffer, Request.OriginalBlobAddress);
        }

        // This is only called while processing incoming requests.  We don't have to worry about cancelling 
        // any response writes.
        internal static void CancelRequest(SafeHandle requestQueueHandle, ulong requestId)
        {
            // It is safe to ignore the return value on a cancel operation because the connection is being closed
            uint statusCode = Interop.HttpApi.HttpCancelHttpRequest(requestQueueHandle, requestId,
                IntPtr.Zero);
        }

        // The request is being aborted, but large writes may be in progress. Cancel them.
        internal void ForceCancelRequest(SafeHandle requestQueueHandle, ulong requestId)
        {
            uint statusCode = Interop.HttpApi.HttpCancelHttpRequest(requestQueueHandle, requestId,
                IntPtr.Zero);

            // Either the connection has already dropped, or the last write is in progress.
            // The requestId becomes invalid as soon as the last Content-Length write starts.
            // The only way to cancel now is with CancelIoEx.
            if (statusCode == Interop.HttpApi.ERROR_CONNECTION_INVALID)
            {
                _response.CancelLastWrite(requestQueueHandle);
            }
        }

        internal void SetAuthenticationHeaders()
        {
            Listener.SetAuthenticationHeaders(this);
        }
    }
}
