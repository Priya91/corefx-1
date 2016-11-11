// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace System.Net
{
    public sealed unsafe partial class HttpListenerResponse : IDisposable
    {
        public void Close()
        {
            //if (NetEventSource.IsEnabled) NetEventSource.Enter(this, "Close", "");
            try
            {
                //if (NetEventSource.IsEnabled) NetEventSource.Info("HttpListenerResponse::Close()");
                ((IDisposable)this).Dispose();
            }
            finally
            {
                //if (NetEventSource.IsEnabled) NetEventSource.Exit(this, "Close", "");
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
