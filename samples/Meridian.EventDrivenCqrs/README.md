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
- transient validation and retry-friendly behaviors

Run:

```bash
dotnet run --project samples/Meridian.EventDrivenCqrs/Meridian.EventDrivenCqrs.csproj -c Release
```
