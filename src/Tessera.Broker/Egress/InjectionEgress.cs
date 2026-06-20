using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
using Tessera.Core.Recipes;
using Tessera.Core.Stores;
using Yarp.ReverseProxy.Forwarder;

namespace Tessera.Broker.Egress;

/// <summary>What the egress decided for a candidate forward.</summary>
public enum EgressDisposition
{
    /// <summary>Egress is disabled (the iteration-1 default).</summary>
    Disabled = 0,

    /// <summary>The recipe is not an HTTP-injectable target.</summary>
    NotHttpEgress,

    /// <summary>The destination host is not on the SSRF allow-list.</summary>
    HostNotAllowed,

    /// <summary>The stored bundle lacks the material to inject.</summary>
    NoCredential,

    /// <summary>The request was forwarded with the injected credential.</summary>
    Forwarded,
}

/// <summary>The outcome of an egress attempt.</summary>
/// <param name="Disposition">What happened.</param>
/// <param name="Error">The forwarder error, if a forward was attempted.</param>
public sealed record EgressOutcome(EgressDisposition Disposition, ForwarderError? Error = null)
{
    /// <summary>True when the request was actually forwarded.</summary>
    public bool Forwarded => Disposition == EgressDisposition.Forwarded;
}

/// <summary>
/// The injection egress (YARP <see cref="IHttpForwarder"/>): inject a stored
/// credential and forward to an allow-listed upstream, returning only the result.
/// OFF by default — deploying the broker never opens an egress path. The inbound
/// caller token is stripped before forwarding (no passthrough — MCP spec).
/// </summary>
public sealed class InjectionEgress : IDisposable
{
    private readonly EgressOptions _options;
    private readonly SsrfGuard _guard;
    private readonly IHttpForwarder _forwarder;
    private readonly HttpMessageInvoker _invoker;

    /// <summary>Creates the egress over the configured options + a forwarder.</summary>
    /// <param name="options">The egress settings (enabled flag + SSRF host allow-list).</param>
    /// <param name="forwarder">The YARP forwarder.</param>
    /// <param name="addressGuard">
    /// The connect-time IP guard (defaults to <see cref="AddressGuard.PublicOnly"/> —
    /// this path reaches only public SaaS, so loopback <em>and</em> private/internal
    /// ranges are refused even on a rebind). Tests pass a permissive guard.
    /// </param>
    public InjectionEgress(EgressOptions options, IHttpForwarder forwarder, AddressGuard? addressGuard = null)
    {
        _options = options;
        _guard = new SsrfGuard(options.AllowedHosts, options.AllowPlainHttp);
        _forwarder = forwarder;
        var address = addressGuard ?? AddressGuard.PublicOnly;
        _invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // Connect-time SSRF pin: resolve the host once, validate the *resolved* IP,
            // and connect to that pinned address — a DNS rebind can't swap in an internal
            // address between the host allow-list check and the socket (HttpClientTransport
            // does the same for the recipe-tool path; this closes it for the proxy path).
            ConnectCallback = (context, ct) => ConnectAsync(address, context, ct),
        });
    }

    /// <summary>
    /// Resolves the target host, rejects any address the <see cref="AddressGuard"/>
    /// blocks, and connects to the first allowed <em>resolved</em> address — pinning it
    /// so the connection lands on the validated IP (no re-resolution, no rebind window).
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(
        AddressGuard guard, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        var anyAllowed = false;
        foreach (var address in addresses)
        {
            if (!guard.IsAllowed(address))
            {
                continue;
            }

            anyAllowed = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        if (!anyAllowed)
        {
            throw new IOException(
                $"egress blocked: host '{host}' resolved only to disallowed addresses (SSRF address guard — link-local/metadata/loopback/private)");
        }

        throw lastError ?? new IOException($"egress: could not connect to '{host}'");
    }

    /// <summary>True when egress is enabled by configuration.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Decides — without forwarding — whether a candidate egress would proceed.</summary>
    public EgressDisposition Evaluate(string destinationPrefix, Recipe recipe, CredentialBundle bundle)
    {
        if (!_options.Enabled)
        {
            return EgressDisposition.Disabled;
        }

        if (recipe.Egress is not (EgressMode.Http or EgressMode.Proxy))
        {
            return EgressDisposition.NotHttpEgress;
        }

        if (!_guard.IsAllowed(destinationPrefix))
        {
            return EgressDisposition.HostNotAllowed;
        }

        if (CredentialInjector.BuildHeaders(bundle, recipe.Injection).Count == 0)
        {
            return EgressDisposition.NoCredential;
        }

        return EgressDisposition.Forwarded;
    }

    /// <summary>True when <paramref name="upstream"/>'s scheme + host pass the SSRF allow-list.</summary>
    public bool IsUpstreamAllowed(Uri upstream) => _guard.IsAllowed(upstream);

    /// <summary>
    /// Injects the credential and reverse-proxies the caller's request verbatim
    /// (method + path + body) to the validated <paramref name="upstream"/>, if the
    /// guards pass. The destination is fixed to <paramref name="upstream"/> and the
    /// caller's identity headers are stripped, so the caller can neither retarget via
    /// the inbound path nor leak its token to the provider.
    /// </summary>
    public async Task<EgressOutcome> ForwardAsync(
        HttpContext context,
        Uri upstream,
        Recipe recipe,
        CredentialBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        var disposition = Evaluate(upstream.AbsoluteUri, recipe, bundle);
        if (disposition != EgressDisposition.Forwarded)
        {
            return new EgressOutcome(disposition);
        }

        var headers = CredentialInjector.BuildHeaders(bundle, recipe.Injection);
        var destinationPrefix = upstream.GetLeftPart(UriPartial.Authority);
        var transformer = new InjectingTransformer(headers, upstream);
        var error = await _forwarder
            .SendAsync(context, destinationPrefix, _invoker, ForwarderRequestConfig.Empty, transformer)
            .ConfigureAwait(false);
        return new EgressOutcome(EgressDisposition.Forwarded, error);
    }

    /// <inheritdoc/>
    public void Dispose() => _invoker.Dispose();

    /// <summary>
    /// Fixes the destination to the validated upstream, strips the caller's token and
    /// every Tessera identity header, and injects the stored credential.
    /// </summary>
    private sealed class InjectingTransformer : HttpTransformer
    {
        private readonly IReadOnlyList<(string Name, string Value)> _headers;
        private readonly Uri _upstream;

        public InjectingTransformer(IReadOnlyList<(string Name, string Value)> headers, Uri upstream)
        {
            _headers = headers;
            _upstream = upstream;
        }

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
                .ConfigureAwait(false);

            // Fix the destination to the validated upstream URL. The caller drives the
            // path/query through the validated X-Tessera-Upstream header, not the inbound
            // /v1/egress route path (which YARP would otherwise append) — so the inbound
            // path can't smuggle a different target past the allow-list.
            proxyRequest.RequestUri = _upstream;
            proxyRequest.Headers.Host = _upstream.Authority;

            // Never pass the caller's own token or any Tessera identity header upstream
            // (MCP spec: no passthrough; the user's Authentik token must never reach the
            // provider).
            proxyRequest.Headers.Remove("Authorization");
            proxyRequest.Headers.Remove("Cookie");
            StripIdentityHeaders(proxyRequest);

            foreach (var (name, value) in _headers)
            {
                proxyRequest.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // Remove every X-Tessera-* header the caller set (the on-behalf-of token, the
        // upstream host, the confirm flag) so none of Tessera's identity plane leaks to
        // the upstream provider.
        private static void StripIdentityHeaders(HttpRequestMessage proxyRequest)
        {
            var toRemove = proxyRequest.Headers
                .Where(h => h.Key.StartsWith("X-Tessera-", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Key)
                .ToArray();
            foreach (var name in toRemove)
            {
                proxyRequest.Headers.Remove(name);
            }
        }
    }
}
