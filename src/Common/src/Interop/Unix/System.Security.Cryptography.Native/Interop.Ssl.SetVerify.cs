// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class Ssl
    {
        internal delegate int SslSetVerifyCallback(int preverify_ok, IntPtr x509_ctx);

        [DllImport(Libraries.CryptoNative, EntryPoint = "CryptoNative_SslSetVerify")]
        internal static extern void SslSetVerify(SafeSslHandle ssl, SslSetVerifyCallback callback);
    }
}

namespace Microsoft.Win32.SafeHandles
{
    internal sealed partial class SafeSslHandle : SafeHandle
    {
        internal string HostName { get; set; }

        internal bool CheckCertName { get; set; }

        internal bool CheckCertRevocation { get; set; }

        internal RemoteCertValidationCallback CertValidationCallback { get; set; }

        internal int VerifyCertificate(int preverify_ok, IntPtr ctx)
        {
            bool success = false;
            X509Chain chain = null;
            X509Certificate2 remoteCertificateEx = null;
            try
            {
                SslPolicyErrors sslPolicyErrors = SslPolicyErrors.None;
                X509Certificate2Collection remoteCertificateStore;
                remoteCertificateEx = CertificateValidationPal.GetRemoteCertificate(new SafeX509StoreCtxHandle(ctx, false), out remoteCertificateStore);
                if (remoteCertificateEx == null)
                {
                    sslPolicyErrors |= SslPolicyErrors.RemoteCertificateNotAvailable;
                }
                else
                {
                    chain = new X509Chain();
                    chain.ChainPolicy.RevocationMode = CheckCertRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
                    chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                    if (remoteCertificateStore != null)
                    {
                        chain.ChainPolicy.ExtraStore.AddRange(remoteCertificateStore);
                    }

                    sslPolicyErrors |= CertificateValidationPal.VerifyCertificateProperties(
                        chain,
                        remoteCertificateEx,
                        CheckCertName,
                        IsServer,
                        HostName);
                }

                if (CertValidationCallback != null)
                {
                    success = CertValidationCallback(HostName, remoteCertificateEx, chain, sslPolicyErrors);
                }
                else
                {
                    success = (sslPolicyErrors == SslPolicyErrors.None);
                }
            }
            finally
            {
                if (chain != null)
                    chain.Dispose();

                if (remoteCertificateEx != null)
                    remoteCertificateEx.Dispose();
            }

            return success ? 1 : 0;
        }
    }
}
