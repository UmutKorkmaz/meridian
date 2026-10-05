# Meridian documentation

> **Unpublished candidate reference.** This site is generated locally by `eng/verify-docs.ps1` from an exact packed
> candidate feed; the version is shown in the site title. It is not a release. Publishing the site, NuGet packages or
> a GitHub release is a separate, explicitly authorized action.

Meridian is two independent .NET libraries for net8.0, net9.0 and net10.0:

* **Meridian.Mediator**: in-process requests, notifications, streams and pipeline behaviors, with an optional
  analyzer for generated, Native AOT friendly dispatch.
* **Meridian.Mapping**: profile-based object mapping with safe-by-default limits and a bundled source generator.

Optional packages add FluentValidation, caching, Redis idempotency, Entity Framework Core transactions and ASP.NET
Core problem details without adding those dependencies to the core packages.

## Start here

| Page | Use it to |
| --- | --- |
| [Capabilities and evidence](capabilities.md) | See every advertised capability with its version status, runnable proof, regression rows and verified boundary. |
| [Migrate from AutoMapper](migrate-from-automapper.md) | Move profile-based mapping; every example is compiled and executed. |
| [Migrate from MediatR](migrate-from-mediatr.md) | Move requests, notifications, streams and behaviors. |
| [Generated mapping](generated-mapping.md) | Choose between runtime, direct Legacy, direct Structural and generated runtime integration. |
| [Optional integrations](integrations.md) | Check dependency minimums and backend requirements of each optional package. |
| [Native AOT](native-aot.md) | See exactly which RID/TFM rows were published and executed natively. |
| [Compatibility policy](compatibility-policy.md) | Understand version, default and deprecation guarantees. |

## API reference

The [API reference](xref:Meridian.Mapping) is generated from the packed `lib/net8.0` assemblies and their XML
documentation; `eng/verify-docs.ps1` checks that the net9.0 and net10.0 assemblies expose the same API first. Common
entry points:

* Mapping: <xref:Meridian.Mapping.IMapper>, <xref:Meridian.Mapping.MapperConfiguration>, <xref:Meridian.Mapping.Profile>,
  <xref:Meridian.Mapping.Extensions.ServiceCollectionExtensions.AddMeridianMapping*>,
  <xref:Meridian.Mapping.GeneratedMappingExtensions.UseGeneratedMappings*>,
  <xref:Meridian.Mapping.MeridianMappingLimitException>.
* Mediator: <xref:Meridian.Mediator.ISender>, <xref:Meridian.Mediator.IPublisher>, <xref:Meridian.Mediator.IStreamSender>,
  <xref:Meridian.Mediator.Extensions.ServiceCollectionExtensions.AddMeridianMediator*>,
  <xref:Meridian.Mediator.Extensions.StandardPipelineExtensions>, <xref:Meridian.Mediator.Behaviors.CacheKeyComposer>,
  <xref:Meridian.Mediator.MediatorMetricsOptions>.

The mapping generator attributes are emitted into your own compilation as internal types, so they are documented in
the [generated mapping guide](generated-mapping.md#generated-symbols) rather than in the API reference.
