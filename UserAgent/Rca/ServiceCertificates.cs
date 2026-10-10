using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Mpai.Rca;

// WHICH CERTIFICATE OF THE SERVICE A CLIENT ACCEPTS (M3248 3.1). A Service on this
// machine's loopback - the Linux package's, behind its host; on Windows, one with
// the development certificate - is accepted as it is: nothing between the two
// leaves the machine. A Service anywhere else presents a certificate this machine
// trusts, and the system checks it.
public static class ServiceCertificates
{
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? Validator(Uri service) =>
        service.IsLoopback ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null;
}
