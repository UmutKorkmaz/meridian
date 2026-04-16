# Event-Driven CQRS Sample

This sample uses the mediator as a command/query bus plus notification events to update a read model.

Code is organized in:

- `Application`: command/query handlers, validators, read projections, and notification handlers
- `Domain`: domain records and in-memory model shapes
- `Infrastructure`: in-memory stores, caching/logging infrastructure, mapping profile, and DI extension

It demonstrates:

- command/query handlers with cache invalidation
- notification-based read model maintenance
- stream request projection
- transient validation plus logging and caching behaviors

Run:

```bash
dotnet run --project samples/Meridian.EventDrivenCqrs/Meridian.EventDrivenCqrs.csproj -c Release
```

This sample focuses on the architectural pattern. For features added in
Meridian v1.1 — `MaxDepth` / `MaxCollectionItems` safety defaults,
`[GenerateMapper]` source generator, `TurkishCulture` helpers,
`AddMeridianStandard`, `AuditBehavior`, and `LocalizedValidationBehavior`
— see [`Meridian.QuickStart`](../Meridian.QuickStart/) and the
`Safety defaults + culture (v1.1)` demo in
[`Meridian.Showcase`](../Meridian.Showcase/).
