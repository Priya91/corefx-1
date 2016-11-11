// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.Net
{
    [Serializable]
    public class HttpListenerException : Win32Exception
    {
        public HttpListenerException() : base(Marshal.GetLastWin32Error())
        {
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, NativeErrorCode.ToString() + ":" + Message);
        }

        public HttpListenerException(int errorCode) : base(errorCode)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, NativeErrorCode.ToString() + ":" + Message);
        }

        public HttpListenerException(int errorCode, string message) : base(errorCode, message)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, NativeErrorCode.ToString() + ":" + Message);
        }
    }
}
