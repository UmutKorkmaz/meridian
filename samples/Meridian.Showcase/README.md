# Meridian Showcase

This sample is a full feature-oriented demonstration of both Mapping and Mediator capabilities.

- Mapping (`MappingDemo`): `Profile`, `ForAllOtherMembers`, `IMemberValueResolver`, `IncludeMembers`, `ReverseMap`, `ProjectTo`
- Mediator query demo (`QueryMediatorDemo`): validation, logging, retry, caching, and correlation
- Mediator command demo (`CommandMediatorDemo`): validation, logging, transaction, authorization, idempotency, cache invalidation
- Notifications demo (`NotificationMediatorDemo`): resilient publisher type with handler failure handling
- Streaming demo (`StreamingMediatorDemo`): open stream behavior

Folders:

- `Demos` contains one file per demo (mapping/query/command/notification/streaming)
- `Shared` contains transport and domain model support

Run from repository root:

```bash
dotnet run --project samples/Meridian.Showcase
```

The sample is split into five demos:

- `Mapping`: profiles, `AddProfiles`, `ForAllOtherMembers`, `IMemberValueResolver`, `IncludeMembers`, `ReverseMap`, and `ProjectTo`
- `Mediator Query`: validation, logging, retry, caching, and correlation
- `Mediator Command`: validation, logging, transaction, authorization, idempotency, and cache invalidation
- `Notifications`: `NotificationPublisherType` with `ResilientTaskWhenAllPublisher`
- `Streaming`: open stream behavior registration with `AddOpenStreamBehavior`
