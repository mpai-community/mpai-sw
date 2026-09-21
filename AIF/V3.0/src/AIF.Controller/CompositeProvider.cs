using System;
using System.Collections.Generic;
using System.Linq;

using AIF.SharedStorage;

namespace AIF.Controller;

// A SERVICE IS AS CAPABLE AS ITS PROVIDERS. One provider knows one application's
// AIMs; a Service offering several Apps holds several, and asks each in turn.
//
// Adding an App to a Service is adding its provider to the list - not editing a
// merged switch that has to be understood before it can be extended.
//
// The question comes before the building. A Service told to offer an App whose
// AIMs it cannot make should say so when it starts, rather than when a person
// chooses it.
public sealed class CompositeProvider : IAimProvider
{
    private readonly List<IAimProvider> providers;

    public CompositeProvider(params IAimProvider[] providers)
        => this.providers = providers.Where(p => p is not null).ToList();

    public CompositeProvider Add(IAimProvider provider)
    {
        if (provider is not null) providers.Add(provider);
        return this;
    }

    public bool CanCreate(string aimName) =>
        providers.Any(p => p.CanCreate(aimName));

    public IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings,
        ISharedStorage? storage)
    {
        foreach (var p in providers)
            if (p.CanCreate(aimName))
                return p.Create(aimName, settings, storage);

        throw new NotSupportedException(
            $"No provider in this Service makes '{aimName}'. " +
            "The Service was told to offer an App whose AIMs it cannot build.");
    }
}