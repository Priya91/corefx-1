// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Test.Common
{
    public class LoopbackServer
    {
        public static Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> AllowAllCertificates = (_, __, ___, ____) => true;

        private enum AuthenticationProtocols
        {
            Basic,
            Digest,
            None
        }

        public class Options
        {
            public IPAddress Address { get; set; } = IPAddress.Loopback;
            public int ListenBacklog { get; set; } = 1;
            public bool UseSsl { get; set; } = false;
            public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;
            public bool WebSocketEndpoint { get; set; } = false;
            public Func<Stream, Stream> ResponseStreamWrapper { get; set; }
            public string Domain { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
        }

        public static Task CreateServerAsync(Func<Socket, Uri, Task> funcAsync, Options options = null)
        {
            IPEndPoint ignored;
            return CreateServerAsync(funcAsync, out ignored, options);
        }

        public static Task CreateServerAsync(Func<Socket, Uri, Task> funcAsync, out IPEndPoint localEndPoint, Options options = null)
        {
            options = options ?? new Options();
            try
            {
                var server = new Socket(options.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

                server.Bind(new IPEndPoint(options.Address, 0));
                server.Listen(options.ListenBacklog);

                localEndPoint = (IPEndPoint)server.LocalEndPoint;
                string host = options.Address.AddressFamily == AddressFamily.InterNetworkV6 ?
                    $"[{localEndPoint.Address}]" :
                    localEndPoint.Address.ToString();

                string scheme = options.UseSsl ? "https" : "http";
                if (options.WebSocketEndpoint)
                {
                    scheme = options.UseSsl ? "wss" : "ws";
                }

                var url = new Uri($"{scheme}://{host}:{localEndPoint.Port}/");

                return funcAsync(server, url).ContinueWith(t =>
                {
                    server.Dispose();
                    t.GetAwaiter().GetResult();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            catch (Exception e)
            {
                localEndPoint = null;
                return Task.FromException(e);
            }
        }

        public static Task CreateServerAndClientAsync(Func<Uri, Task> clientFunc, Func<Socket, Task> serverFunc)
        {
            return CreateServerAsync(async (server, uri) =>
            {
                Task clientTask = clientFunc(uri);
                Task serverTask = serverFunc(server);

                await new Task[] { clientTask, serverTask }.WhenAllOrAnyFailed();
            });
        }

        public static string DefaultHttpResponse => $"HTTP/1.1 200 OK\r\nDate: {DateTimeOffset.UtcNow:R}\r\nContent-Length: 0\r\n\r\n";

        public static IPAddress GetIPv6LinkLocalAddress() =>
            NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.IsIPv6LinkLocal)
                .FirstOrDefault();

        public static Task<List<string>> ReadRequestAndSendResponseAsync(Socket server, string response = null, Options options = null)
        {
            return AcceptSocketAsync(server, (s, stream, reader, writer) => ReadWriteAcceptedAsync(s, reader, writer, response), options);
        }

        public static Task<List<string>> ReadRequestAndAuthenticateAsync(Socket server, string response, Options options)
        {
            return AcceptSocketAsync(server, (s, stream, reader, writer) => ValidateAuthenticationAsync(s, reader, writer, response, options), options);
        }

        public static async Task<List<string>> ReadWriteAcceptedAsync(Socket s, StreamReader reader, StreamWriter writer, string response = null)
        {
            // Read request line and headers. Skip any request body.
            var lines = new List<string>();
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
            {
                lines.Add(line);
            }

            await writer.WriteAsync(response ?? DefaultHttpResponse).ConfigureAwait(false);

            return lines;
        }

        public static async Task<List<string>> ValidateAuthenticationAsync(Socket s, StreamReader reader, StreamWriter writer, string response, Options options)
        {
            // Send unauthorized response from server.
            await ReadWriteAcceptedAsync(s, reader, writer, response);

            // Read the request method.
            string line = await reader.ReadLineAsync().ConfigureAwait(false);
            int index = line != null ? line.IndexOf(' ') : -1;
            string requestMethod = null;
            if (index != -1)
            {
                requestMethod = line.Substring(0, index);
            }

            // Read the authorization header from client.
            AuthenticationProtocols protocol = AuthenticationProtocols.None;
            string clientResponse = null;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
            {
                if (line.StartsWith("Authorization"))
                {
                    clientResponse = line;
                    if (line.Contains(nameof(AuthenticationProtocols.Basic)))
                    {
                        protocol = AuthenticationProtocols.Basic;
                        break;
                    }
                    else if (line.Contains(nameof(AuthenticationProtocols.Digest)))
                    {
                        protocol = AuthenticationProtocols.Digest;
                        break;
                    }
                }
            }

            bool success = false;
            switch (protocol)
            {
                case AuthenticationProtocols.Basic:
                    success = IsBasicAuthTokenValid(line, options);
                    break;

                case AuthenticationProtocols.Digest:
                    // Read the request content.
                    string requestContent = null;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        if (line.Contains("Content-Length"))
                        {
                            line = await reader.ReadLineAsync().ConfigureAwait(false);
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                            {
                                requestContent += line;
                            }
                        }
                    }

                    success = IsDigestAuthTokenValid(clientResponse, requestContent, requestMethod, options);
                    break;
            }

            if (success)
            {
                await writer.WriteAsync(DefaultHttpResponse).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(response).ConfigureAwait(false);
            }

            return null;
        }

        private static bool IsBasicAuthTokenValid(string clientResponse, Options options)
        {
            string clientHash = clientResponse.Substring(clientResponse.IndexOf(nameof(AuthenticationProtocols.Basic), StringComparison.OrdinalIgnoreCase) +
                nameof(AuthenticationProtocols.Basic).Length).Trim();
            string userPass = string.IsNullOrEmpty(options.Domain) ? options.Username + ":" + options.Password : options.Domain + "\\" + options.Username + ":" + options.Password;
            return clientHash == Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass));
        }

        private static bool IsDigestAuthTokenValid(string clientResponse, string requestContent, string requestMethod, Options options)
        {
            string clientHash = clientResponse.Substring(clientResponse.IndexOf(nameof(AuthenticationProtocols.Digest), StringComparison.OrdinalIgnoreCase) +
                nameof(AuthenticationProtocols.Digest).Length).Trim();
            string[] values = clientHash.Split(',');

            string username = null, uri = null, realm = null, nonce = null, response = null, algorithm = null, cnonce = null, opaque = null, qop = null, nc = null;
            bool userhash = false;
            for (int i = 0; i < values.Length; i++)
            {
                string trimmedValue = values[i].Trim();
                if (trimmedValue.Contains(nameof(username)))
                {
                    // Username is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');

                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        username = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }

                    // Username is mandatory.
                    if (string.IsNullOrEmpty(username))
                        return false;
                }
                if (trimmedValue.Contains(nameof(userhash)) && trimmedValue.Contains("true"))
                {
                    userhash = true;
                }
                else if (trimmedValue.Contains(nameof(uri)))
                {
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        uri = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }

                    // Request uri is mandatory.
                    if (string.IsNullOrEmpty(uri))
                        return false;
                }
                else if (trimmedValue.Contains(nameof(realm)))
                {
                    // Realm is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        realm = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }

                    // Realm is mandatory.
                    if (string.IsNullOrEmpty(realm))
                        return false;
                }
                else if (trimmedValue.Contains(nameof(cnonce)))
                {
                    // CNonce is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        cnonce = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }
                }
                else if (trimmedValue.Contains(nameof(nonce)))
                {
                    // Nonce is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        nonce = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }

                    // Nonce is mandatory.
                    if (string.IsNullOrEmpty(nonce))
                        return false;
                }
                else if (trimmedValue.Contains(nameof(response)))
                {
                    // response is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        response = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }

                    // Response is mandatory.
                    if (string.IsNullOrEmpty(response))
                        return false;
                }
                else if (trimmedValue.Contains(nameof(algorithm)))
                {
                    int startIndex = trimmedValue.IndexOf('=');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        algorithm = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex).Trim();
                    }

                    if (string.IsNullOrEmpty(algorithm))
                        algorithm = "sha-256";
                }
                else if (trimmedValue.Contains(nameof(opaque)))
                {
                    // Opaque is a quoted string.
                    int startIndex = trimmedValue.IndexOf('"');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        opaque = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex - 1);
                    }
                }
                else if (trimmedValue.Contains(nameof(qop)))
                {
                    int startIndex = trimmedValue.IndexOf('=');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        qop = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex).Trim();
                    }
                }
                else if (trimmedValue.Contains(nameof(nc)))
                {
                    int startIndex = trimmedValue.IndexOf('=');
                    if (startIndex != -1)
                    {
                        startIndex += 1;
                        nc = trimmedValue.Substring(startIndex, trimmedValue.Length - startIndex).Trim();
                    }
                }
            }

            // Verify username.
            if (userhash && ComputeHash(options.Username + ":" + realm, algorithm) != username)
            {
                return false;
            }

            if (!userhash && options.Username != username)
            {
                return false;
            }

            // Calculate response and compare with the client response hash.
            string a1 = options.Username + ":" + realm + ":" + options.Password;
            if (algorithm.Contains("sess"))
            {
                a1 = ComputeHash(a1, algorithm) + ":" + nonce + ":" + cnonce ?? string.Empty;
            }

            string a2 = requestMethod + ":" + uri;
            if (qop.Equals("auth-int"))
            {
                string content = requestContent ?? string.Empty;
                a2 = a2 + ":" + ComputeHash(content, algorithm);
            }

            string serverResponseHash = ComputeHash(ComputeHash(a1, algorithm) + ":" +
                                        nonce + ":" +
                                        nc + ":" +
                                        cnonce + ":" +
                                        qop + ":" +
                                        ComputeHash(a2, algorithm), algorithm);

            return response == serverResponseHash;
        }

        private static string ComputeHash(string data, string algorithm)
        {
            // Disable MD5 insecure warning.
#pragma warning disable CA5351
            using (HashAlgorithm hash = algorithm.Contains("SHA-256") ? SHA256.Create() : (HashAlgorithm)MD5.Create())
#pragma warning restore CA5351
            {
                Encoding enc = Encoding.UTF8;
                byte[] result = hash.ComputeHash(enc.GetBytes(data));

                StringBuilder sb = new StringBuilder(result.Length * 2);
                foreach (byte b in result)
                    sb.Append(b.ToString("x2"));

                return sb.ToString();
            }
        }

        public static async Task<bool> WebSocketHandshakeAsync(Socket s, StreamReader reader, StreamWriter writer)
        {
            string serverResponse = null;
            string currentRequestLine;
            while (!string.IsNullOrEmpty(currentRequestLine = await reader.ReadLineAsync().ConfigureAwait(false)))
            {
                string[] tokens = currentRequestLine.Split(new char[] { ':' }, 2);
                if (tokens.Length == 2)
                {
                    string headerName = tokens[0];
                    if (headerName == "Sec-WebSocket-Key")
                    {
                        string headerValue = tokens[1].Trim();
                        string responseSecurityAcceptValue = ComputeWebSocketHandshakeSecurityAcceptValue(headerValue);
                        serverResponse =
                            "HTTP/1.1 101 Switching Protocols\r\n" +
                            "Upgrade: websocket\r\n" +
                            "Connection: Upgrade\r\n" +
                            "Sec-WebSocket-Accept: " + responseSecurityAcceptValue + "\r\n\r\n";
                    }
                }
            }

            if (serverResponse != null)
            {
                // We received a valid WebSocket opening handshake. Send the appropriate response.
                await writer.WriteAsync(serverResponse).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private static string ComputeWebSocketHandshakeSecurityAcceptValue(string secWebSocketKey)
        {
            // GUID specified by RFC 6455.
            const string Rfc6455Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            string combinedKey = secWebSocketKey + Rfc6455Guid;

            // Use of SHA1 hash is required by RFC 6455.
            SHA1 sha1Provider = new SHA1CryptoServiceProvider();
            byte[] sha1Hash = sha1Provider.ComputeHash(Encoding.UTF8.GetBytes(combinedKey));
            return Convert.ToBase64String(sha1Hash);
        }

        public static async Task<List<string>> AcceptSocketAsync(Socket server, Func<Socket, Stream, StreamReader, StreamWriter, Task<List<string>>> funcAsync, Options options = null)
        {
            options = options ?? new Options();
            Socket s = await server.AcceptAsync().ConfigureAwait(false);
            s.NoDelay = true;
            try
            {
                Stream stream = new NetworkStream(s, ownsSocket: false);
                if (options.UseSsl)
                {
                    var sslStream = new SslStream(stream, false, delegate { return true; });
                    using (var cert = Configuration.Certificates.GetServerCertificate())
                    {
                        await sslStream.AuthenticateAsServerAsync(
                            cert,
                            clientCertificateRequired: true, // allowed but not required
                            enabledSslProtocols: options.SslProtocols,
                            checkCertificateRevocation: false).ConfigureAwait(false);
                    }
                    stream = sslStream;
                }

                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(options?.ResponseStreamWrapper?.Invoke(stream) ?? stream, Encoding.ASCII) { AutoFlush = true })
                {
                    return await funcAsync(s, stream, reader, writer).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    s.Shutdown(SocketShutdown.Send);
                    s.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // In case the test itself disposes of the socket
                }
            }
        }

        public enum TransferType
        {
            None = 0,
            ContentLength,
            Chunked
        }

        public enum TransferError
        {
            None = 0,
            ContentLengthTooLarge,
            ChunkSizeTooLarge,
            MissingChunkTerminator
        }

        public static Task StartTransferTypeAndErrorServer(
            TransferType transferType,
            TransferError transferError,
            out IPEndPoint localEndPoint)
        {
            return CreateServerAsync((server, url) => AcceptSocketAsync(server, async (client, stream, reader, writer) =>
            {
                // Read past request headers.
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()));

                // Determine response transfer headers.
                string transferHeader = null;
                string content = "This is some response content.";
                if (transferType == TransferType.ContentLength)
                {
                    transferHeader = transferError == TransferError.ContentLengthTooLarge ?
                        $"Content-Length: {content.Length + 42}\r\n" :
                        $"Content-Length: {content.Length}\r\n";
                }
                else if (transferType == TransferType.Chunked)
                {
                    transferHeader = "Transfer-Encoding: chunked\r\n";
                }

                // Write response header
                await writer.WriteAsync("HTTP/1.1 200 OK\r\n").ConfigureAwait(false);
                await writer.WriteAsync($"Date: {DateTimeOffset.UtcNow:R}\r\n").ConfigureAwait(false);
                await writer.WriteAsync("Content-Type: text/plain\r\n").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(transferHeader))
                {
                    await writer.WriteAsync(transferHeader).ConfigureAwait(false);
                }
                await writer.WriteAsync("\r\n").ConfigureAwait(false);

                // Write response body
                if (transferType == TransferType.Chunked)
                {
                    string chunkSizeInHex = string.Format(
                        "{0:x}\r\n",
                        content.Length + (transferError == TransferError.ChunkSizeTooLarge ? 42 : 0));
                    await writer.WriteAsync(chunkSizeInHex).ConfigureAwait(false);
                    await writer.WriteAsync($"{content}\r\n").ConfigureAwait(false);
                    if (transferError != TransferError.MissingChunkTerminator)
                    {
                        await writer.WriteAsync("0\r\n\r\n").ConfigureAwait(false);
                    }
                }
                else
                {
                    await writer.WriteAsync($"{content}").ConfigureAwait(false);
                }

                return null;
            }), out localEndPoint);
        }
    }
}
