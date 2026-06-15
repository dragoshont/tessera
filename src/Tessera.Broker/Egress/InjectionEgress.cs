using System.Net;
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
    public InjectionEgress(EgressOptions options, IHttpForwarder forwarder)
    {
        _options = options;
        _guard = new SsrfGuard(options.AllowedHosts, options.AllowPlainHttp);
        _forwarder = forwarder;
        _invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
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

        if (recipe.Egress != EgressMode.Http)
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

    /// <summary>Injects the credential and forwards the request, if the guards pass.</summary>
    public async Task<EgressOutcome> ForwardAsync(
        HttpContext context,
        string destinationPrefix,
        Recipe recipe,
        CredentialBundle bundle)
    {
        var disposition = Evaluate(destinationPrefix, recipe, bundle);
        if (disposition != EgressDisposition.Forwarded)
        {
            return new EgressOutcome(disposition);
        }

        var headers = CredentialInjector.BuildHeaders(bundle, recipe.Injection);
        var transformer = new InjectingTransformer(headers);
        var error = await _forwarder
            .SendAsync(context, destinationPrefix, _invoker, ForwarderRequestConfig.Empty, transformer)
            .ConfigureAwait(false);
        return new EgressOutcome(EgressDisposition.Forwarded, error);
    }

    /// <inheritdoc/>
    public void Dispose() => _invoker.Dispose();

    /// <summary>Strips the inbound caller token and injects the stored credential.</summary>
    private sealed class InjectingTransformer : HttpTransformer
    {
        private readonly IReadOnlyList<(string Name, string Value)> _headers;

        public InjectingTransformer(IReadOnlyList<(string Name, string Value)> headers) => _headers = headers;

        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext,
            HttpRequestMessage proxyRequest,
            string destinationPrefix,
            CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
                .ConfigureAwait(false);

            // Never pass the caller's own token upstream (MCP spec: no passthrough).
            proxyRequest.Headers.Remove("Authorization");
            proxyRequest.Headers.Remove("Cookie");

            foreach (var (name, value) in _headers)
            {
                proxyRequest.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }
}
